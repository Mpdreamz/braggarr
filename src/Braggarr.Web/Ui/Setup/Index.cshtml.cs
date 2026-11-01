using Braggarr.Adapters.Git;
using Braggarr.Adapters.GitHub;
using Braggarr.Web.Auth;
using Braggarr.Web.Settings;

namespace Braggarr.Web.Ui.Setup;

/// <summary>
/// The first run, in steps: name the posts repo, create Braggarr's GitHub App from a manifest, install it on the repo, sign in with
/// GitHub (which makes you the owner), and put the starter files in the repo. Open only with the setup token, or as the owner.
/// </summary>
public sealed partial class IndexModel(SettingsStore settings, IGitHubToken token, GitHubClient github, ILogger<IndexModel> logger) : UiPage
{
	public BraggarrSettings Settings => settings.Current;

	public bool CanSetUp => User.HasClaim(Roles.ClaimType, Roles.Setup) || User.HasClaim(Roles.ClaimType, Roles.Owner);

	public string Manifest { get; private set; } = "";

	public string State { get; private set; } = "";

	public bool Installed { get; private set; }

	public bool Prepared { get; private set; }

	public string? Problem { get; private set; }

	public string? Repository { get; set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		if (!CanSetUp)
		{
			return;
		}
		Manifest = GitHubSignIn.Manifest(HttpContext);
		State = GitHubSignIn.NewState(HttpContext);
		if (Settings.GitHubApp.AppId > 0 && Settings.PostsRepository is not null)
		{
			try
			{
				Installed = await token.GetAsync(ct) is not null;
				Prepared = Installed && await ExistsAsync("README.md", ct);
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				Installed = false;
				if (logger.IsEnabled(LogLevel.Information))
				{
					logger.LogInformation("The app is not installed on {Repository} yet: {Problem}", Settings.PostsRepository, e.Message);
				}
			}
		}
	}

	public async Task<IResult> OnPostRepositoryAsync(CancellationToken ct)
	{
		if (!CanSetUp)
		{
			return Forbid();
		}
		var name = Repository?.Trim().Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/') ?? "";
		if (!RepositoryName().IsMatch(name))
		{
			Problem = "Name the repository as owner/name, for example owner/braggarr-posts.";
			await OnGetAsync(ct);
			return Page();
		}
		_ = settings.Update(s => s with { PostsRepository = name });
		return Redirect("/Setup");
	}

	/// <summary>Puts a README and a starter voice.md in the posts repo, so main exists and Claude has something to read later.</summary>
	public async Task<IResult> OnPostPrepareAsync(CancellationToken ct)
	{
		if (!CanSetUp)
		{
			return Forbid();
		}
		await PutIfMissingAsync("README.md", Starter.Readme, ct);
		await PutIfMissingAsync("voice.md", Starter.Voice, ct);
		return Redirect("/Setup");
	}

	protected internal override void Bind(Binder form)
	{
		if (form.Has(nameof(Repository)))
		{
			Repository = form.Text(nameof(Repository));
		}
	}

	protected internal override Task<IResult>? Handle(string method, string? handler, Binder form, CancellationToken ct) => (method, handler) switch
	{
		("GET", null) => Shown(OnGetAsync(ct)),
		("POST", "repository") => OnPostRepositoryAsync(ct),
		("POST", "prepare") => OnPostPrepareAsync(ct),
		_ => null
	};

	protected override RazorSlices.RazorSlice Slice() => Index.Create(this);

	private async Task<bool> ExistsAsync(string path, CancellationToken ct)
	{
		try
		{
			_ = await github.GetAsync<System.Text.Json.JsonElement>(github.Repo($"contents/{path}"), ct);
			return true;
		}
		catch (GitHubException e) when (e.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
		{
			return false;
		}
	}

	private async Task PutIfMissingAsync(string path, string content, CancellationToken ct)
	{
		if (!await ExistsAsync(path, ct))
		{
			_ = await github.PutAsync<System.Text.Json.JsonElement>(
				github.Repo($"contents/{path}"),
				new { message = $"Add {path}", content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content)) },
				ct
			);
		}
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
	private static partial System.Text.RegularExpressions.Regex RepositoryName();
}

internal static class Starter
{
	public const string Readme = """
		# Braggarr's posts

		Campaigns live in `campaigns/<slug>/`: `campaign.md` holds the brief, each other `.md` file is one post with a `## network`
		section per network. Braggarr writes `deliveries.yml` next to them; leave that file to it.

		A campaign is proposed as a pull request. Merging it approves it.
		""";

	public const string Voice = """
		# Voice

		How posts should sound. Braggarr's drafting reads this file.

		- Plain and specific: what it does, for whom, and one concrete detail.
		- No hype words, no "excited to announce".
		- One link, at the end.
		""";
}
