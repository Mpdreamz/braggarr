using Braggarr.Cli;
using Nullean.Argh;

var app = new ArghApp();
app.UseCliDescription("Braggarr: campaigns about what you build, and the blog they write to.");
app.Map<ServerCommands>();
app.MapNamespace<BlogCommands>("blog");
// As the GitHub Action, the inputs come as INPUT_* variables and there are no arguments.
return await app.RunAsync(args.Length == 0 && GitHubActions.IsRunning(Environment.GetEnvironmentVariable)
	? GitHubActions.Args(Environment.GetEnvironmentVariable)
	: args);
