using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braggarr.Domain;
using Braggarr.Services;

namespace Braggarr.Adapters.Social;

/// <summary>
/// Reads and changes the owner's profiles with each account's own credentials. Bluesky keeps a profile as a record in the
/// account's repository; Mastodon, X and YouTube have an endpoint for it; Threads and LinkedIn can only be read.
/// </summary>
public sealed class NetworkProfiles(IHttpClientFactory http, AccountStore accounts, TimeProvider time) : IProfiles
{

	public Task<AccountProfile> ReadAsync(Network network, string account, CancellationToken ct) => network switch
	{
		Network.Bluesky => BlueskyAsync(account, null, ct),
		Network.Mastodon => MastodonAsync(account, null, ct),
		Network.X => XAsync(account, null, ct),
		Network.YouTube => YouTubeAsync(account, null, ct),
		Network.Threads => ThreadsAsync(account, ct),
		Network.LinkedIn => LinkedInAsync(account, ct),
		_ => throw new PublishException($"{network} has no profile Braggarr reads.")
	};

	public async Task ApplyAsync(Network network, string account, ProfileChange change, CancellationToken ct) =>
		_ = network switch
		{
			Network.Bluesky => await BlueskyAsync(account, change, ct),
			Network.Mastodon => await MastodonAsync(account, change, ct),
			Network.X => await XAsync(account, change, ct),
			Network.YouTube => await YouTubeAsync(account, change, ct),
			_ => throw new PublishException($"{network} does not let Braggarr change a profile.")
		};

	private T Credentials<T>(Network network, string account) where T : class =>
		accounts.Get<T>(network, account) ?? throw new PublishException($"No {network} account \"{account}\" is connected.");

	private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string network, CancellationToken ct)
	{
		if (!response.IsSuccessStatusCode)
		{
			throw new PublishException($"{network} refused ({(int)response.StatusCode}): {Trim(await response.Content.ReadAsStringAsync(ct))}");
		}
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
	}

	private static string Trim(string text) => text.Length > 300 ? text[..300] : text;

	private static string? Text(JsonElement element, string property) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	// Bluesky: the profile is the record app.bsky.actor.profile/self in the account's repository. A change reads the record and
	// writes it back with only the changed fields different, so the picture and banner it points at stay.
	private async Task<AccountProfile> BlueskyAsync(string account, ProfileChange? change, CancellationToken ct)
	{
		var credentials = Credentials<BlueskyCredentials>(Network.Bluesky, account);
		var client = http.CreateClient("bluesky");
		var service = credentials.Service.TrimEnd('/');
		using var signIn = await client.PostAsJsonAsync($"{service}/xrpc/com.atproto.server.createSession",
			new BlueskyPublisher.SignIn(credentials.Handle, credentials.AppPassword), WebJson.Default.SignIn, ct);
		var session = await ReadJsonAsync(signIn, "Bluesky", ct);
		var did = session.GetProperty("did").GetString()!;
		var token = session.GetProperty("accessJwt").GetString()!;

		if (change is not null)
		{
			using var get = new HttpRequestMessage(HttpMethod.Get,
				$"{service}/xrpc/com.atproto.repo.getRecord?repo={Uri.EscapeDataString(did)}&collection=app.bsky.actor.profile&rkey=self");
			get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using var existing = await client.SendAsync(get, ct);
			JsonObject record;
			string? cid = null;
			if (existing.StatusCode == HttpStatusCode.BadRequest)
			{
				// No profile record yet: the first change makes one.
				record = new JsonObject { ["$type"] = "app.bsky.actor.profile" };
			}
			else
			{
				var found = await ReadJsonAsync(existing, "Bluesky", ct);
				record = JsonNode.Parse(found.GetProperty("value").GetRawText())!.AsObject();
				cid = Text(found, "cid");
			}
			if (change.DisplayName is { } name)
			{
				record["displayName"] = name;
			}
			if (change.Bio is { } bio)
			{
				record["description"] = bio;
			}
			var body = new JsonObject { ["repo"] = did, ["collection"] = "app.bsky.actor.profile", ["rkey"] = "self", ["record"] = record };
			if (cid is not null)
			{
				body["swapRecord"] = cid;
			}
			using var put = new HttpRequestMessage(HttpMethod.Post, $"{service}/xrpc/com.atproto.repo.putRecord") { Content = WebJson.Body(body) };
			put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using var written = await client.SendAsync(put, ct);
			_ = await ReadJsonAsync(written, "Bluesky", ct);
		}

		using var read = new HttpRequestMessage(HttpMethod.Get, $"{service}/xrpc/app.bsky.actor.getProfile?actor={Uri.EscapeDataString(did)}");
		read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		using var answer = await client.SendAsync(read, ct);
		var profile = await ReadJsonAsync(answer, "Bluesky", ct);
		var handle = Text(profile, "handle") ?? credentials.Handle;
		return new AccountProfile(Network.Bluesky, account, Text(profile, "displayName"), Text(profile, "description"), null,
			Text(profile, "avatar") is not null, Text(profile, "banner") is not null)
		{ Url = $"https://bsky.app/profile/{handle}" };
	}

	// Mastodon: verify_credentials has the plain text of the bio and the profile fields under "source". A change sends every
	// field again, since update_credentials replaces them all; the link is the first field whose value is a web address.
	private async Task<AccountProfile> MastodonAsync(string account, ProfileChange? change, CancellationToken ct)
	{
		var credentials = Credentials<MastodonCredentials>(Network.Mastodon, account);
		var client = http.CreateClient("mastodon");
		var instance = credentials.Instance.TrimEnd('/');
		async Task<JsonElement> MeAsync()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, $"{instance}/api/v1/accounts/verify_credentials");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
			using var response = await client.SendAsync(request, ct);
			return await ReadJsonAsync(response, "Mastodon", ct);
		}
		var me = await MeAsync();
		var fields = FieldsOf(me);
		if (change is not null)
		{
			var form = new List<KeyValuePair<string, string>>();
			if (change.DisplayName is { } name)
			{
				form.Add(new("display_name", name));
			}
			if (change.Bio is { } bio)
			{
				form.Add(new("note", bio));
			}
			if (change.Link is { } link)
			{
				var at = fields.FindIndex(x => x.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase));
				if (at >= 0)
				{
					fields[at] = (fields[at].Name, link);
				}
				else if (fields.Count < 4)
				{
					fields.Add(("Website", link));
				}
				else
				{
					throw new PublishException("The Mastodon profile already has four fields: make room for a website first.");
				}
				for (var i = 0; i < fields.Count; i++)
				{
					form.Add(new($"fields_attributes[{i}][name]", fields[i].Name));
					form.Add(new($"fields_attributes[{i}][value]", fields[i].Value));
				}
			}
			using var request = new HttpRequestMessage(HttpMethod.Patch, $"{instance}/api/v1/accounts/update_credentials") { Content = new FormUrlEncodedContent(form) };
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
			using var response = await client.SendAsync(request, ct);
			if (response.StatusCode == HttpStatusCode.Forbidden)
			{
				throw new PublishException("Mastodon refused: the access token needs the write:accounts scope to change the profile. Make a new one with it.");
			}
			_ = await ReadJsonAsync(response, "Mastodon", ct);
			me = await MeAsync();
			fields = FieldsOf(me);
		}
		var note = me.TryGetProperty("source", out var s) ? Text(s, "note") : Text(me, "note");
		static bool Set(string? url) => url is not null && !url.Contains("missing.png", StringComparison.Ordinal);
		return new AccountProfile(Network.Mastodon, account, Text(me, "display_name") is { Length: > 0 } dn ? dn : null, note,
			fields.FirstOrDefault(x => x.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)).Value, Set(Text(me, "avatar")), Set(Text(me, "header")))
		{ Url = Text(me, "url") };
	}

	/// <summary>The profile fields in plain text, as the owner typed them.</summary>
	private static List<(string Name, string Value)> FieldsOf(JsonElement me) =>
		me.TryGetProperty("source", out var source) && source.TryGetProperty("fields", out var fields)
			? [.. fields.EnumerateArray().Select(x => (Text(x, "name") ?? "", Text(x, "value") ?? ""))]
			: [];

	// X: the v1.1 account endpoints, signed with OAuth 1.0a over the query string and the form fields. The website comes back
	// shortened (t.co); its expanded address is in the entities.
	private async Task<AccountProfile> XAsync(string account, ProfileChange? change, CancellationToken ct)
	{
		var keys = Credentials<XCredentials>(Network.X, account);
		var client = http.CreateClient("x");
		HttpRequestMessage Signed(HttpMethod method, string url, IReadOnlyList<KeyValuePair<string, string>> form)
		{
			var request = new HttpRequestMessage(method, url) { Content = form.Count > 0 ? new FormUrlEncodedContent(form) : null };
			var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
			var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
			request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", OAuth1.Header(method.Method, url, form, keys, nonce, timestamp));
			return request;
		}
		if (change is not null)
		{
			var form = new List<KeyValuePair<string, string>>();
			if (change.DisplayName is { } name)
			{
				form.Add(new("name", name));
			}
			if (change.Bio is { } bio)
			{
				form.Add(new("description", bio));
			}
			if (change.Link is { } link)
			{
				form.Add(new("url", link));
			}
			using var update = Signed(HttpMethod.Post, $"{XPublisher.Api}/1.1/account/update_profile.json", form);
			using var updated = await client.SendAsync(update, ct);
			_ = await ReadJsonAsync(updated, "X", ct);
		}
		using var request = Signed(HttpMethod.Get, $"{XPublisher.Api}/1.1/account/verify_credentials.json?include_entities=true&skip_status=true", []);
		using var response = await client.SendAsync(request, ct);
		var me = await ReadJsonAsync(response, "X", ct);
		var expanded = me.TryGetProperty("entities", out var entities) && entities.TryGetProperty("url", out var u) && u.TryGetProperty("urls", out var urls)
			? urls.EnumerateArray().Select(x => Text(x, "expanded_url")).FirstOrDefault(x => x is not null)
			: null;
		var screenName = Text(me, "screen_name") ?? keys.Username;
		return new AccountProfile(Network.X, account, Text(me, "name"), Text(me, "description"), expanded ?? Text(me, "url"),
			!(me.TryGetProperty("default_profile_image", out var d) && d.ValueKind == JsonValueKind.True), Text(me, "profile_banner_url") is not null)
		{ Url = $"https://x.com/{screenName}" };
	}

	// YouTube: the channel's description is in brandingSettings.channel, which an update replaces whole, so it is sent back with
	// only the description changed. Whether the channel has its own picture is not something the API tells.
	private async Task<AccountProfile> YouTubeAsync(string account, ProfileChange? change, CancellationToken ct)
	{
		var credentials = Credentials<YouTubeCredentials>(Network.YouTube, account);
		var client = http.CreateClient("youtube");
		var (token, _) = await GoogleOAuth.RefreshAsync(client, credentials, ct);
		async Task<JsonElement> ChannelAsync()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet,brandingSettings&mine=true");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using var response = await client.SendAsync(request, ct);
			var answer = await ReadJsonAsync(response, "YouTube", ct);
			return answer.TryGetProperty("items", out var items) && items.GetArrayLength() > 0
				? items[0]
				: throw new PublishException("YouTube found no channel for this account.");
		}
		var channel = await ChannelAsync();
		if (change?.Bio is { } bio)
		{
			var branding = channel.TryGetProperty("brandingSettings", out var b) && b.TryGetProperty("channel", out var c)
				? JsonNode.Parse(c.GetRawText())!.AsObject()
				: [];
			branding["description"] = bio;
			var body = new JsonObject { ["id"] = channel.GetProperty("id").GetString(), ["brandingSettings"] = new JsonObject { ["channel"] = branding } };
			using var request = new HttpRequestMessage(HttpMethod.Put, "https://www.googleapis.com/youtube/v3/channels?part=brandingSettings") { Content = WebJson.Body(body) };
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using var response = await client.SendAsync(request, ct);
			if (response.StatusCode == HttpStatusCode.Forbidden)
			{
				throw new PublishException("YouTube refused: connect the channel again, so Braggarr may manage it (the youtube scope), not only upload.");
			}
			_ = await ReadJsonAsync(response, "YouTube", ct);
			channel = await ChannelAsync();
		}
		var snippet = channel.GetProperty("snippet");
		var banner = channel.TryGetProperty("brandingSettings", out var settings) && settings.TryGetProperty("image", out var image) && Text(image, "bannerExternalUrl") is not null;
		var handle = Text(snippet, "customUrl");
		return new AccountProfile(Network.YouTube, account, Text(snippet, "title"), Text(snippet, "description"), null, null, banner)
		{
			Url = handle is not null ? $"https://www.youtube.com/{handle}" : $"https://www.youtube.com/channel/{channel.GetProperty("id").GetString()}"
		};
	}

	private async Task<AccountProfile> ThreadsAsync(string account, CancellationToken ct)
	{
		var credentials = Credentials<ThreadsCredentials>(Network.Threads, account);
		using var response = await http.CreateClient("threads").GetAsync(
			new Uri($"{ThreadsPublisher.Graph}/v1.0/me?fields=id,username,name,threads_biography,threads_profile_picture_url&access_token={Uri.EscapeDataString(credentials.AccessToken)}"), ct);
		var me = await ReadJsonAsync(response, "Threads", ct);
		var username = Text(me, "username") ?? credentials.Username;
		return new AccountProfile(Network.Threads, account, Text(me, "name"), Text(me, "threads_biography"), null, Text(me, "threads_profile_picture_url") is not null, null)
		{ Url = $"https://www.threads.com/@{username}" };
	}

	// LinkedIn's API with these scopes tells the name and picture only: not the headline or the about section.
	private async Task<AccountProfile> LinkedInAsync(string account, CancellationToken ct)
	{
		var credentials = Credentials<LinkedInCredentials>(Network.LinkedIn, account);
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
		using var response = await http.CreateClient("linkedin").SendAsync(request, ct);
		var me = await ReadJsonAsync(response, "LinkedIn", ct);
		return new AccountProfile(Network.LinkedIn, account, Text(me, "name"), null, null, Text(me, "picture") is not null, null);
	}
}
