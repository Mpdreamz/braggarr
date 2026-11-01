using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Html.Dom;
using AwesomeAssertions;

namespace Braggarr.Web.Tests;

/// <summary>
/// What the owner's pages answer, byte for byte: every page in the states the sample data gives it, and every form on those pages
/// submitted as rendered, as a browser would, with each of its buttons. A submission records its status, where it redirects and
/// the page it shows, then the page the redirect leads to. Headers are recorded too, cookie values aside. The files in <c>pages/</c> were written by the Razor Pages UI
/// (<c>BRAGGARR_WRITE_GOLDEN=1</c>), so a UI rebuilt on other foundations shows here wherever it answers differently.
/// </summary>
public partial class PageGoldens
{
	private static readonly string[] SignedIn =
	[
		"/", "/Index", "/?show=active", "/?show=held", "/?show=done", "/Campaigns", "/Campaigns/Index",
		"/Calendar", "/Calendar?month=1", "/Calendar?month=-1", "/Calendar?rebalance=true", "/calendar",
		"/Activity", "/Activity?tab=queue", "/Activity?tab=history", "/Activity?tab=events", "/History",
		"/Projects", "/Projects/mermaider", "/Projects/curb", "/Projects/nothing-here",
		"/System",
		"/Settings", "/Settings?section=networks", "/Settings?section=claude", "/Settings?section=judge", "/Settings?section=agents",
		"/Settings?section=general", "/Settings?youtube=connected", "/Settings?youtube=failed", "/Settings/Profiles", "/settings/profiles",
		"/Campaigns/New",
		"/Campaigns/Proposal?number=1", "/Campaigns/Proposal?number=99", "/Campaigns/Proposal",
		"/Campaigns/Detail?slug=curb-1-0", "/Campaigns/Detail?slug=heed-0-21", "/Campaigns/Detail?slug=hunch-0-3", "/Campaigns/Detail?slug=nope",
		"/Campaigns/Detail",
		"/Logout", "/Denied", "/Setup", "/Nope", "/Calendar/"
	];

	private static readonly string[] Anonymous = ["/", "/Calendar", "/Settings", "/Setup", "/Denied", "/Logout", "/Nope"];

	private static string Golden(string name, string actual, [CallerFilePath] string here = "")
	{
		var path = Path.Combine(Path.GetDirectoryName(here)!, "pages", name);
		if (Environment.GetEnvironmentVariable("BRAGGARR_WRITE_GOLDEN") == "1")
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, actual);
			return "";
		}
		if (File.Exists(path) && File.ReadAllText(path) == actual)
		{
			return "";
		}
		// Beside the golden, to diff against it; git ignores it.
		File.WriteAllText(path + ".actual", actual);
		return path;
	}

	private static string Name(string page) =>
		page == "/" ? "home" : Unsafe().Replace(page.Trim('/'), "-") + (page.EndsWith('/') ? "-slash" : "");

	[GeneratedRegex("[^A-Za-z0-9_-]+")]
	private static partial Regex Unsafe();

	[GeneratedRegex("""name="__RequestVerificationToken" type="hidden" value="[^"]*" """, RegexOptions.None)]
	private static partial Regex Token();

	[GeneratedRegex("[0-9a-f]{32}")]
	private static partial Regex Hex();

	[GeneratedRegex("brag_[A-Za-z0-9_-]{16,}")]
	private static partial Regex Key();

	[GeneratedRegex("<dt>Version</dt><dd>[^<]*</dd>")]
	private static partial Regex Version();

	/// <summary>What changes between runs and means nothing: the anti-forgery token, random states, new API keys, the build's version and the factory's own folder.</summary>
	private static string Normalise(string text, BraggarrFactory factory) =>
		Key().Replace(Hex().Replace(Version().Replace(Token().Replace(text, """name="__RequestVerificationToken" type="hidden" value="…" """), "<dt>Version</dt><dd>…</dd>"), "…hex…"), "brag_…")
			.Replace(factory.ConfigDirectory, "{config}", StringComparison.Ordinal);

	/// <summary>Headers that say how the bytes travelled, not what was answered.</summary>
	private static readonly HashSet<string> Transport = new(StringComparer.OrdinalIgnoreCase) { "Date", "Content-Length", "Transfer-Encoding" };

	[GeneratedRegex("^([^=]+)=[^;]*")]
	private static partial Regex CookieValue();

	private static async Task<string> RecordAsync(HttpResponseMessage response, BraggarrFactory factory)
	{
		var text = new StringBuilder();
		_ = text.Append((int)response.StatusCode).Append(' ').Append(response.StatusCode).Append('\n');
		foreach (var (name, values) in response.Headers.Concat(response.Content.Headers).Where(h => !Transport.Contains(h.Key)).OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
		{
			foreach (var value in values)
			{
				// A cookie's value is new each time; its name and attributes are what the page decided.
				_ = text.Append(name).Append(": ").Append(name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? CookieValue().Replace(value, "$1=…") : value).Append('\n');
			}
		}
		_ = text.Append('\n');
		var body = await response.Content.ReadAsStringAsync();
		// A crash shows the developer exception page, with this machine's paths and the request's cookies: the exception is enough.
		_ = text.Append(response.StatusCode == HttpStatusCode.InternalServerError ? body.Split('\n')[0] : body);
		return Normalise(text.ToString(), factory);
	}

	/// <summary>Requests no page sends: without the anti-forgery token, with a handler that does not exist, other methods, a stranger posting.</summary>
	[Test]
	public async Task Unusual_requests_answer_as_before()
	{
		var mismatches = new List<string>();
		using (var factory = new BraggarrFactory())
		{
			var browser = await Screens.SeededAsync(factory);
			var token = await BraggarrFactory.FormTokenAsync(browser, "/Settings");
			HttpRequestMessage Form(HttpMethod method, string url, bool withToken = true) => new(method, url)
			{
				Content = new FormUrlEncodedContent(withToken ? [new("__RequestVerificationToken", token)] : Array.Empty<KeyValuePair<string, string>>())
			};
			var requests = new (string Name, HttpClient Client, Func<HttpRequestMessage> Request)[]
			{
				("post-without-token", browser, () => Form(HttpMethod.Post, "/Settings?section=general&handler=Sync", withToken: false)),
				("post-unknown-handler", browser, () => Form(HttpMethod.Post, "/Calendar?handler=Nope")),
				("post-no-handler", browser, () => Form(HttpMethod.Post, "/")),
				("post-handler-in-form", browser, () => new HttpRequestMessage(HttpMethod.Post, "/") { Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("handler", "Sync")]) }),
				("get-unknown-handler", browser, () => new HttpRequestMessage(HttpMethod.Get, "/Campaigns/Detail?slug=curb-1-0&handler=Nope")),
				("head", browser, () => new HttpRequestMessage(HttpMethod.Head, "/Calendar")),
				("put", browser, () => Form(HttpMethod.Put, "/Calendar")),
				("delete-without-token", browser, () => new HttpRequestMessage(HttpMethod.Delete, "/Calendar")),
				("get-slug-in-other-case", browser, () => new HttpRequestMessage(HttpMethod.Get, "/Campaigns/Detail?slug=CURB-1-0")),
				("get-project-extra-segment", browser, () => new HttpRequestMessage(HttpMethod.Get, "/Projects/curb/extra")),
				("stranger-post", factory.Browser(), () => Form(HttpMethod.Post, "/Settings?handler=Sync", withToken: false)),
				("stranger-head", factory.Browser(), () => new HttpRequestMessage(HttpMethod.Head, "/Settings")),
			};
			foreach (var (name, client, request) in requests)
			{
				using var message = request();
				mismatches.Add(Golden($"unusual/{name}.txt", $"{message.Method} {message.RequestUri}\n" + await RecordAsync(await client.SendAsync(message), factory)));
			}
		}
		mismatches.Where(m => m.Length > 0).Should().BeEmpty();
	}

	[Test]
	public async Task Every_page_answers_as_before()
	{
		var mismatches = new List<string>();
		using (var factory = new BraggarrFactory())
		{
			var browser = await Screens.SeededAsync(factory);
			foreach (var (page, i) in SignedIn.Select((p, i) => (p, i)))
			{
				mismatches.Add(Golden($"get/{i:00}-{Name(page)}.txt", await RecordAsync(await browser.GetAsync(page), factory)));
			}
			var stranger = factory.Browser();
			foreach (var (page, i) in Anonymous.Select((p, i) => (p, i)))
			{
				mismatches.Add(Golden($"anonymous/{i:00}-{Name(page)}.txt", await RecordAsync(await stranger.GetAsync(page), factory)));
			}
		}
		mismatches.Where(m => m.Length > 0).Should().BeEmpty();
	}

	/// <summary>
	/// Values as no form sends them, each on fresh sample data, to hold how they bind: numbers in hex, repeated, blank or not
	/// numbers; flags in other spellings; networks by number, in lowercase or unknown; keys of a dictionary in lowercase.
	/// </summary>
	[Test]
	public async Task Odd_values_bind_as_before()
	{
		var probes = new (string Name, string Url, (string, string)[]? Form)[]
		{
			("month-hex", "/Calendar?month=0x1", null),
			("month-hash", "/Calendar?month=%231", null),
			("month-not-a-number", "/Calendar?month=abc", null),
			("month-blank", "/Calendar?month=%20", null),
			("month-twice", "/Calendar?month=1&month=2", null),
			("month-decimal", "/Calendar?month=1.0", null),
			("rebalance-upper", "/Calendar?rebalance=TRUE", null),
			("rebalance-on", "/Calendar?rebalance=on", null),
			("rebalance-padded", "/Calendar?rebalance=%20true%20", null),
			("tab-case", "/Activity?tab=History", null),
			("number-padded", "/Campaigns/Proposal?number=%201%20", null),
			("repeat-hex", "/Campaigns/Detail?slug=curb-1-0&handler=Repeat", [("months", "0x3")]),
			("repeat-blank", "/Campaigns/Detail?slug=curb-1-0&handler=Repeat", [("months", "")]),
			("network-lowercase", "/Campaigns/Detail?slug=curb-1-0&handler=Posted", [("post", "01-launch"), ("network", "hackernews")]),
			("network-number", "/Campaigns/Detail?slug=curb-1-0&handler=Posted", [("post", "01-launch"), ("network", "6")]),
			("network-undefined", "/Campaigns/Detail?slug=curb-1-0&handler=Posted", [("post", "01-launch"), ("network", "99")]),
			("texts-lowercase-key", "/Campaigns/Detail?slug=heed-0-21&handler=Edit", [("post", "02-follow-up"), ("at", "2030-01-13T09:00"), ("texts[bluesky]", "Changed."), ("texts[nope]", "x")]),
			("texts-lowercase-key-alone", "/Campaigns/Detail?slug=heed-0-21&handler=Edit", [("post", "02-follow-up"), ("at", "2030-01-13T09:00"), ("texts[bluesky]", "Changed.")]),
			("texts-unknown-key-beside", "/Campaigns/Detail?slug=heed-0-21&handler=Edit", [("post", "02-follow-up"), ("at", "2030-01-13T09:00"), ("texts[Bluesky]", "Changed."), ("texts[nope]", "x")]),
			("texts-number-key", "/Campaigns/Detail?slug=heed-0-21&handler=Edit", [("post", "02-follow-up"), ("at", "2030-01-13T09:00"), ("texts[0]", "Changed.")]),
			("networks-unknown", "/Projects?handler=Watch", [("repository", "nullean/fresh"), ("networks", "bluesky"), ("networks", "nope"), ("networks", "")]),
			("slug-from-query-and-form", "/Campaigns/Detail?slug=curb-1-0&handler=Repeat", [("slug", "heed-0-21"), ("months", "6")]),
			("flag-twice", "/Projects?handler=Watch", [("repository", "nullean/fresh"), ("networks", "Bluesky"), ("prereleases", "false"), ("prereleases", "true")]),
		};
		var mismatches = new List<string>();
		foreach (var (name, url, fields) in probes)
		{
			using var factory = new BraggarrFactory();
			var browser = await Screens.SeededAsync(factory);
			HttpRequestMessage request;
			if (fields is null)
			{
				request = new HttpRequestMessage(HttpMethod.Get, url);
			}
			else
			{
				var token = await BraggarrFactory.FormTokenAsync(browser, "/Settings");
				request = new HttpRequestMessage(HttpMethod.Post, url)
				{
					Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))])
				};
			}
			using (request)
			{
				var response = await browser.SendAsync(request);
				var text = $"{request.Method} {url}\n" + await RecordAsync(response, factory);
				if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next && !next.IsAbsoluteUri)
				{
					text += $"\n\n==== GET {next.OriginalString}\n" + await RecordAsync(await browser.GetAsync(next), factory);
				}
				mismatches.Add(Golden($"binding/{name}.txt", text));
			}
		}
		mismatches.Where(m => m.Length > 0).Should().BeEmpty();
	}

	/// <summary>
	/// One form on one page, sent with one of its buttons (or with none, when it has none), its required fields filled as a browser
	/// insists, or left empty as a client that skips the browser's checks would send them.
	/// </summary>
	public sealed record Submission(string Page, int Form, int Button, string Label, bool Filled = true);

	/// <summary>Every form on every page that has one, and every button of each, as the sample data renders them.</summary>
	public static async Task<List<Submission>> SubmissionsAsync()
	{
		using var factory = new BraggarrFactory();
		var browser = await Screens.SeededAsync(factory);
		var found = new List<Submission>();
		foreach (var page in SignedIn.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var response = await browser.GetAsync(page);
			if (response.StatusCode != HttpStatusCode.OK)
			{
				continue;
			}
			var document = await ParseAsync(page, await response.Content.ReadAsStringAsync());
			var forms = document.Forms.ToList();
			for (var f = 0; f < forms.Count; f++)
			{
				if (!string.Equals(forms[f].Method, "post", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				var buttons = Buttons(forms[f]);
				for (var b = 0; b < Math.Max(1, buttons.Count); b++)
				{
					var label = buttons.Count == 0 ? "no-button" : Unsafe().Replace((buttons[b].GetAttribute("formaction") ?? "") + "-" + buttons[b].TextContent.Trim(), "-").Trim('-');
					found.Add(new Submission(page, f, buttons.Count == 0 ? -1 : b, label));
					if (Required(forms[f]))
					{
						found.Add(new Submission(page, f, buttons.Count == 0 ? -1 : b, label + "-empty", Filled: false));
					}
				}
			}
		}
		return found;
	}

	private static List<AngleSharp.Dom.IElement> Buttons(IHtmlFormElement form) =>
		[.. form.Elements.Where(e => e is IHtmlButtonElement { Type: "submit" } or IHtmlInputElement { Type: "submit" or "image" })];

	private static async Task<IHtmlDocument> ParseAsync(string page, string html)
	{
		var context = BrowsingContext.New(Configuration.Default);
		return (IHtmlDocument)await context.OpenAsync(r => r.Content(html).Address("http://localhost" + page));
	}

	[Test]
	public async Task Every_form_answers_as_before()
	{
		var submissions = await SubmissionsAsync();
		submissions.Should().NotBeEmpty();
		var mismatches = new List<string>();
		var counts = new Dictionary<string, int>();
		foreach (var submission in submissions)
		{
			var key = $"{Name(submission.Page)}/{submission.Form:00}-{submission.Label}";
			counts[key] = counts.GetValueOrDefault(key) + 1;
			var name = counts[key] == 1 ? key : $"{key}-{counts[key]}";
			mismatches.Add(Golden($"post/{name}.txt", await SubmitAsync(submission)));
		}
		mismatches.Where(m => m.Length > 0).Should().BeEmpty();
	}

	private static bool Required(IHtmlFormElement form) =>
		form.Elements.Any(e => e is IHtmlInputElement { IsRequired: true, Value.Length: 0 } or IHtmlTextAreaElement { IsRequired: true, Value.Length: 0 });

	/// <summary>A required field left empty stops a browser from sending the form, so it gets a plain value of its kind.</summary>
	private static void Fill(IHtmlFormElement form)
	{
		foreach (var element in form.Elements)
		{
			switch (element)
			{
				case IHtmlInputElement { IsRequired: true, Value.Length: 0 } input:
					input.Value = input.Type switch
					{
						"url" => "https://example.com/",
						"email" => "owner@example.com",
						"number" => input.Minimum is { Length: > 0 } min ? min : "1",
						"date" => "2030-01-08",
						"datetime-local" => "2030-01-08T10:00",
						_ => "x"
					};
					break;
				case IHtmlTextAreaElement { IsRequired: true, Value.Length: 0 } text:
					text.Value = "x";
					break;
			}
		}
	}

	/// <summary>On fresh sample data: open the page, send the form as rendered, then follow a redirect once.</summary>
	private static async Task<string> SubmitAsync(Submission submission)
	{
		using var factory = new BraggarrFactory();
		var browser = await Screens.SeededAsync(factory);
		var document = await ParseAsync(submission.Page, await browser.GetStringAsync(submission.Page));
		var form = document.Forms[submission.Form];
		var button = submission.Button < 0 ? null : Buttons(form)[submission.Button] as IHtmlElement;
		if (submission.Filled)
		{
			Fill(form);
		}
		// AngleSharp finds a valid datetime-local value invalid; with the required fields filled, nothing else is left to stop it.
		form.NoValidate = true;
		var request = button is null ? form.GetSubmission() : form.GetSubmission(button);
		if (request is null)
		{
			return "not sent";
		}

		// A browser sends to the button's formaction when it has one; AngleSharp does not. And it keeps a query without its question mark.
		var url = button?.GetAttribute("formaction") is { } action ? new AngleSharp.Dom.Url(new AngleSharp.Dom.Url(document.DocumentUri), action) : request.Target;
		var target = url.PathName + (string.IsNullOrEmpty(url.Query) ? "" : "?" + url.Query);
		using var message = new HttpRequestMessage(new HttpMethod(request.Method.ToString().ToUpperInvariant()), target);
		var body = new MemoryStream();
		await request.Body.CopyToAsync(body);
		message.Content = new ByteArrayContent(body.ToArray());
		_ = message.Content.Headers.TryAddWithoutValidation("Content-Type", request.Headers.TryGetValue("Content-Type", out var type) ? type : "application/x-www-form-urlencoded");

		var response = await browser.SendAsync(message);
		var text = $"{message.Method} {target}\n" + await RecordAsync(response, factory);
		if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next && !next.IsAbsoluteUri)
		{
			text += $"\n\n==== GET {next.OriginalString}\n" + await RecordAsync(await browser.GetAsync(next), factory);
		}
		return text;
	}
}
