using Braggarr.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Braggarr.Services;

/// <summary>
/// Turns the schedule into posts. Each tick syncs the posts repo when it is due, sends what is due, and records every change in
/// the repo before moving on, so a restart can always tell what happened. One tick runs at a time.
/// </summary>
public sealed class SchedulingService(
	ICampaignRepository repository,
	IPublishers publishers,
	IAccounts accounts,
	IManualPosts manual,
	INotifier notifier,
	IDeliveryJournal journal,
	IVideoStore videos,
	ISites sites,
	IProjectVisibility visibility,
	TimeProvider time,
	IOptions<SchedulingOptions> options,
	ILogger<SchedulingService> logger
) : IDisposable
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly SchedulingOptions _options = options.Value;
	private DateTimeOffset? _lastSync;
	private volatile bool _syncRequested;

	/// <summary>The schedule as of the last tick. Read by the UI and the API.</summary>
	public Schedule Current { get; private set; } = Schedule.Empty;

	public DateTimeOffset? LastSync => _lastSync;

	/// <summary>Makes the next tick fetch the posts repo, for after a merge, a hold or "sync now".</summary>
	public void RequestSync() => _syncRequested = true;

	/// <summary>Reads the repo, and marks anything that was sending when Braggarr stopped as unknown: it is never sent again blindly.</summary>
	public async Task StartAsync(CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			await SyncAsync(ct);
			foreach (var interrupted in Current.Deliveries.Where(d => d.State == DeliveryState.Sending).ToArray())
			{
				if (interrupted.IsBlog)
				{
					// Committing again finds the post already there if it got in, so it waits again: nothing to ask the owner.
					await SaveAsync(interrupted.Recover(), new DeliveryEvent.Requeued(interrupted.Key), ct);
					continue;
				}
				await SaveAsync(interrupted.Recover(), new DeliveryEvent.MarkedUnknown(interrupted.Key), ct);
				await notifier.NotifyAsync(
					new Notice(
						NoticeLevel.Problem,
						"Did this post go out?",
						$"Braggarr stopped while posting {interrupted.Key}. Check the network, then tell Braggarr whether it is there."
					),
					ct
				);
			}
			await PushAsync(ct);
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	public async Task TickAsync(CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			var now = time.GetUtcNow();
			if (_syncRequested || _lastSync is null || now - _lastSync >= _options.SyncInterval)
			{
				await SyncAsync(ct);
			}
			Current = Current.Retry(now);
			// First, so a post that links a blog post that just went live goes out in this tick.
			await CheckCommittedAsync(now, ct);
			foreach (var due in Current.Due(now))
			{
				await DeliverAsync(due, ct);
			}
			await CloseManualAsync(ct);
			await PushAsync(ct);
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	/// <summary>The owner looked: the post is on the network after all.</summary>
	public Task ConfirmPublishedAsync(DeliveryKey key, string url, CancellationToken ct) =>
		ResolveAsync(key, d => d.ConfirmPublished(new Receipt(url, url)), d => new DeliveryEvent.Published(d.Key, d.Receipt!), ct);

	/// <summary>The owner looked: the post is not there, so it may go out again.</summary>
	public Task ConfirmMissingAsync(DeliveryKey key, CancellationToken ct) =>
		ResolveAsync(key, d => d.ConfirmMissing(), d => new DeliveryEvent.Failed(d.Key, "Confirmed missing by the owner."), ct);

	/// <summary>The owner posted a Reddit or Hacker News post: its issue is closed and the delivery is done.</summary>
	public async Task MarkManualDoneAsync(DeliveryKey key, CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			if (Current.Find(key) is { State: DeliveryState.ManualDue, Issue: { } issue } due)
			{
				await manual.CloseAsync(issue, ct);
				await SaveAsync(due.CloseManual(), new DeliveryEvent.ManualClosed(key), ct);
				await PushAsync(ct);
			}
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	private async Task ResolveAsync(DeliveryKey key, Func<Delivery, Delivery> change, Func<Delivery, DeliveryEvent> describe, CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			if (Current.Find(key) is { State: DeliveryState.Unknown } unknown && change(unknown) is var next && next != unknown)
			{
				await SaveAsync(next, describe(next), ct);
				await PushAsync(ct);
			}
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	/// <summary>Reads the posts repo again now, without delivering anything: after Braggarr itself wrote to main.</summary>
	public async Task RefreshAsync(CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			await SyncAsync(ct);
		}
		finally
		{
			_ = _gate.Release();
		}
	}

	private async Task SyncAsync(CancellationToken ct)
	{
		_syncRequested = false;
		var state = await repository.PullAsync(ct);
		var now = time.GetUtcNow();
		var campaigns = new List<Campaign>();
		foreach (var campaign in state.Campaigns)
		{
			// On main with no approval yet: merged on GitHub (or committed there) by the owner, the only one who writes to main.
			// The stamp records what was merged, so an edit afterwards undoes it.
			if (campaign.Status is CampaignStatus.Approved or CampaignStatus.Held && campaign.Approval is null)
			{
				var stamp = new Approval(MergedOnGitHub.ToString(), campaign.Digest, now);
				await repository.StampAsync(campaign.Slug, stamp, ct);
				campaigns.Add(campaign with { Approval = stamp });
				continue;
			}
			campaigns.Add(campaign);
		}
		Current = new Schedule([], state.Deliveries.ToValueList()).Sync(campaigns);
		_lastSync = now;
	}

	/// <summary>Who approved a campaign that reached main without Braggarr: the owner, merging on GitHub.</summary>
	public static readonly Actor MergedOnGitHub = Actor.Owner("github");

	private async Task DeliverAsync(Delivery due, CancellationToken ct)
	{
		var campaign = Current.Campaign(due.Key.Campaign)!;
		var post = campaign.Posts.First(p => p.Id == due.Key.Post);
		var variant = post.For(due.Key.Network)!;
		// A post that links the blog post is only due once that is live, at its receipt's address.
		if (variant.LinksToBlog() && Current.BlogPost(campaign)?.Receipt is { } blog)
		{
			variant = variant with { Text = variant.Text.Replace(Blogging.Placeholder, blog.Url, StringComparison.Ordinal) };
		}

		if (due.IsManual)
		{
			var opened = await manual.OpenAsync(campaign, post, variant, ct);
			await SaveAsync(due.OpenManual(opened.Issue), new DeliveryEvent.ManualOpened(due.Key, opened.Issue), ct);
			var network = due.Key.Network == Network.HackerNews ? "Hacker News" : due.Key.Network.ToString();
			await notifier.NotifyAsync(
				new Notice(NoticeLevel.Action, $"Time to post on {network}: {campaign.Title}", Lengths.Title(variant.Text), opened.Submit.Count > 0 ? opened.Submit[0].Url : opened.IssueUrl)
				{
					// The submit links first; the issue last, to close once it is up.
					Actions = [.. opened.Submit.Take(2), new SubmitLink("Issue", opened.IssueUrl)]
				},
				ct
			);
			return;
		}

		// The campaign's account, chosen when it was proposed; a campaign from before accounts posts as today's default.
		var account = campaign.AccountFor(due.Key.Network) ?? accounts.Choose(due.Key.Network, campaign.Project)?.Account;
		if (account is null || publishers.Find(due.Key.Network, account) is not { } publisher)
		{
			if (logger.IsEnabled(LogLevel.Debug))
			{
				logger.LogDebug("{Delivery} waits: {Network} account {Account} is not connected.", due.Key, due.Key.Network, account ?? "(none)");
			}
			return;
		}

		// Recorded before the call: if Braggarr stops during it, the next start sees "sending" and asks instead of resending.
		var started = due.Begin(time.GetUtcNow());
		await SaveAsync(started, new DeliveryEvent.Started(started.Key, started.Attempts), ct);

		// A blog post stays up, so it is only ever about a project anyone can see; asked again at the moment it goes out.
		if (started.IsBlog && !campaign.MayBlog(await visibility.OfAsync(campaign, ct)))
		{
			const string refused = "Only a public project gets a blog post, and GitHub does not say this one is public. Take the blog out of the campaign, or make the repository public.";
			await SaveAsync(started.Fail(refused), new DeliveryEvent.Failed(started.Key, refused), ct);
			await notifier.NotifyAsync(new Notice(NoticeLevel.Problem, $"{campaign.Title}: the blog post did not go out", refused), ct);
			return;
		}

		using var timeout = new CancellationTokenSource(variant.Media.Any(m => m.IsStoredVideo) ? _options.VideoPublishTimeout : _options.PublishTimeout, time);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
		Delivery finished;
		DeliveryEvent outcome;
		try
		{
			var media = await ReadMediaAsync(campaign.Slug, variant, ct);
			var receipt = await publisher.PublishAsync(new PublishRequest(started.Key, post.At, variant) { Media = media }, linked.Token);
			(finished, outcome) = started.IsBlog
				? (started.Commit(receipt), new DeliveryEvent.Committed(started.Key, receipt))
				: (started.Succeed(receipt), (DeliveryEvent)new DeliveryEvent.Published(started.Key, receipt));
		}
		catch (PublishException e)
		{
			// The network answered, and the answer was no: nothing was posted, so a retry cannot post twice.
			finished = started.Fail(e.Message);
			outcome = new DeliveryEvent.Failed(started.Key, e.Message);
		}
		catch (Exception e) when (!ct.IsCancellationRequested)
		{
			// A timeout or a broken connection: the post may or may not be up. Only the owner can tell, unless it is a blog post,
			// which committing again finds there if it got in.
			logger.LogWarning(e, "{Delivery}: no answer from {Network}.", started.Key, started.Key.Network);
			finished = started.Recover();
			outcome = started.IsBlog ? new DeliveryEvent.Requeued(started.Key) : new DeliveryEvent.MarkedUnknown(started.Key);
		}

		await SaveAsync(finished, outcome, ct);
		if (finished.State == DeliveryState.Unknown || (finished.IsSettled && finished.State == DeliveryState.Failed))
		{
			await notifier.NotifyAsync(
				new Notice(NoticeLevel.Problem, $"{campaign.Title}: {finished.Key.Network} needs you", finished.Error ?? "No answer from the network."),
				ct
			);
		}
	}

	/// <summary>
	/// Each committed blog post: live once its address answers, which lets the posts that link it go out; after an hour without
	/// an answer, the owner's to look at.
	/// </summary>
	private async Task CheckCommittedAsync(DateTimeOffset now, CancellationToken ct)
	{
		foreach (var committed in Current.Deliveries.Where(d => d is { State: DeliveryState.Committed, Receipt: not null }).ToArray())
		{
			if (await AnswersAsync(committed.Receipt!.Url, ct))
			{
				await SaveAsync(committed.GoLive(), new DeliveryEvent.WentLive(committed.Key), ct);
			}
			else if (committed.LastAttempt is { } sent && now - sent >= Blogging.LiveWithin)
			{
				await SaveAsync(committed.TimeOut(), new DeliveryEvent.TimedOut(committed.Key), ct);
				var title = Current.Campaign(committed.Key.Campaign)?.Title ?? committed.Key.Campaign;
				await notifier.NotifyAsync(
					new Notice(
						NoticeLevel.Problem,
						$"{title}: the blog post did not appear",
						$"It was committed, but {committed.Receipt.Url} did not answer within an hour. Check the site's build, then tell Braggarr whether the post is there. The posts that link it wait."
					),
					ct
				);
			}
		}
	}

	private async Task<bool> AnswersAsync(string url, CancellationToken ct)
	{
		try
		{
			return await sites.AnswersAsync(url, ct);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			if (logger.IsEnabled(LogLevel.Information))
			{
				logger.LogInformation("{Url} does not answer yet: {Problem}", url, e.Message);
			}
			return false;
		}
	}

	private async Task<IReadOnlyList<MediaFile>> ReadMediaAsync(string slug, Variant variant, CancellationToken ct)
	{
		var files = new List<MediaFile>();
		// A slot still wanted when the post is due goes out without it: the text stands on its own. A blog post's cover goes
		// beside it too.
		IEnumerable<MediaRef> cover = variant.Article?.Cover is { } c && variant.Media.All(m => m.Path != c.Path) ? [c] : [];
		foreach (var image in variant.Media.Where(m => !m.IsWanted).Concat(cover))
		{
			if (image.IsStoredVideo)
			{
				var video = await videos.ReadAsync(image.Path, ct)
					?? throw new PublishException($"The video {image.Path} is not on this server (under /config/media). Upload it again, or take it out of the post.");
				files.Add(new MediaFile(image.Path, "video/mp4", video, image.Alt) { Video = Mp4.Read(video) });
				continue;
			}
			var bytes = await repository.ReadMediaAsync(slug, image.Path, ct)
				?? throw new PublishException($"The image {image.Path} is not in campaigns/{slug}. Add it, or take it out of the post.");
			var type = MediaFile.ContentTypeOf(image.Path) ?? throw new PublishException($"{image.Path} is not a PNG, JPEG, WebP or GIF.");
			files.Add(new MediaFile(image.Path, type, bytes, image.Alt));
		}
		return files;
	}

	private async Task CloseManualAsync(CancellationToken ct)
	{
		var open = Current.Deliveries.Where(d => d is { State: DeliveryState.ManualDue, Issue: not null }).ToArray();
		if (open.Length == 0)
		{
			return;
		}
		var closed = await manual.ClosedAsync([.. open.Select(d => d.Issue!.Value)], ct);
		foreach (var done in open.Where(d => closed.Contains(d.Issue!.Value)))
		{
			await SaveAsync(done.CloseManual(), new DeliveryEvent.ManualClosed(done.Key), ct);
		}
	}

	private async Task SaveAsync(Delivery delivery, DeliveryEvent change, CancellationToken ct)
	{
		await repository.RecordAsync(delivery, ct);
		Current = Current.With(delivery);
		journal.Record(change, time.GetUtcNow());
	}

	private async Task PushAsync(CancellationToken ct)
	{
		try
		{
			await repository.PushAsync(ct);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			// Everything is committed locally; the next tick pushes it.
			logger.LogWarning(e, "Could not push the posts repo; will retry.");
		}
	}

	public void Dispose() => _gate.Dispose();
}
