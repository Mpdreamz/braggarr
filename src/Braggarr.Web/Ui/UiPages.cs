using Braggarr.Web.Auth;
using Microsoft.AspNetCore.Antiforgery;

namespace Braggarr.Web.Ui;

/// <summary>
/// The owner's pages as endpoints, each at the addresses Razor Pages gave it, and for the owner only unless said otherwise.
/// Each answers any HTTP method, as a Razor Page did: every method but GET, HEAD, OPTIONS and TRACE must carry the anti-forgery
/// token (else 400), then the page's properties are bound, then its handler runs.
/// </summary>
public static class UiPages
{
	public static IServiceCollection AddUi(this IServiceCollection services)
	{
		_ = services.AddTransient<IndexModel>();
		_ = services.AddTransient<Campaigns.IndexModel>();
		_ = services.AddTransient<HistoryModel>();
		_ = services.AddTransient<LogoutModel>();
		_ = services.AddTransient<DeniedModel>();
		_ = services.AddTransient<SystemModel>();
		_ = services.AddTransient<ActivityModel>();
		_ = services.AddTransient<ProjectsModel>();
		_ = services.AddTransient<ProjectModel>();
		_ = services.AddTransient<CalendarModel>();
		_ = services.AddTransient<Campaigns.NewModel>();
		_ = services.AddTransient<Campaigns.DetailModel>();
		_ = services.AddTransient<Campaigns.ProposalModel>();
		_ = services.AddTransient<Settings.IndexModel>();
		_ = services.AddTransient<Settings.ProfilesModel>();
		_ = services.AddTransient<Setup.IndexModel>();
		return services;
	}

	public static void MapUi(this IEndpointRouteBuilder app)
	{
		string[] owner = [Roles.OwnerPolicy];
		Map<IndexModel>(app, owner, "/", "/Index");
		Map<Campaigns.IndexModel>(app, owner, "/Campaigns", "/Campaigns/Index");
		Map<HistoryModel>(app, owner, "/History");
		Map<LogoutModel>(app, [Roles.OwnerPolicy, Roles.SetupPolicy], "/Logout");
		Map<DeniedModel>(app, [], "/Denied");
		Map<SystemModel>(app, owner, "/System");
		Map<ActivityModel>(app, owner, "/Activity");
		Map<ProjectsModel>(app, owner, "/Projects");
		Map<ProjectModel>(app, owner, "/Projects/{project}");
		Map<CalendarModel>(app, owner, "/Calendar");
		Map<Campaigns.NewModel>(app, owner, "/Campaigns/New");
		Map<Campaigns.DetailModel>(app, owner, "/Campaigns/Detail");
		// A video is uploaded through the page too: up to 100 MB.
		Map<Campaigns.ProposalModel>(app, owner, "/Campaigns/Proposal")
			.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Api.CampaignApi.MaxUploadBytes))
			.WithFormOptions(multipartBodyLengthLimit: Api.CampaignApi.MaxUploadBytes);
		Map<Settings.IndexModel>(app, owner, "/Settings", "/Settings/Index");
		Map<Settings.ProfilesModel>(app, owner, "/Settings/Profiles");
		// Open to anyone: it asks for the setup token, or the owner, itself.
		Map<Setup.IndexModel>(app, [], "/Setup", "/Setup/Index");
	}

	/// <summary>A page at its addresses, for whoever meets every one of <paramref name="policies"/>; anyone when there are none.</summary>
	private static RouteGroupBuilder Map<T>(IEndpointRouteBuilder app, string[] policies, params string[] patterns) where T : UiPage
	{
		var group = app.MapGroup("");
		_ = policies.Length == 0 ? group.AllowAnonymous() : group.RequireAuthorization(policies);
		foreach (var pattern in patterns)
		{
			_ = group.Map(pattern, RunAsync<T>);
		}
		return group;
	}

	private static async Task RunAsync<T>(HttpContext http) where T : UiPage
	{
		var method = http.Request.Method;
		if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)))
		{
			try
			{
				await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
			}
			catch (AntiforgeryValidationException)
			{
				http.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}
		}
		IFormCollection? form = null;
		if (http.Request.HasFormContentType)
		{
			try
			{
				form = await http.Request.ReadFormAsync(http.RequestAborted);
			}
			catch (Exception e) when (e is InvalidDataException or IOException)
			{
				http.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}
		}

		var page = http.RequestServices.GetRequiredService<T>();
		page.HttpContext = http;
		var binder = new Binder(http, form);
		if (!HttpMethods.IsGet(method))
		{
			page.Bound = binder;
			page.Bind(binder);
		}
		var verb = HttpMethods.IsHead(method) ? "GET" : method.ToUpperInvariant();
		var handler = http.Request.Query.TryGetValue("handler", out var names) && !string.IsNullOrEmpty(names[0]) ? names[0]!.ToLowerInvariant() : null;
		var handled = page.Handle(verb, handler, binder, http.RequestAborted);
		if (handled is null && handler is not null)
		{
			handled = page.Handle(verb, null, binder, http.RequestAborted);
		}
		var result = handled is null ? page.Page() : await handled;
		await result.ExecuteAsync(http);
	}
}
