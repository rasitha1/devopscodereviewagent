using CodeReviewAgent.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<ReviewCommand>();
app.Configure(config =>
{
    config.SetApplicationName("ai-code-review");
    config.SetApplicationVersion("1.0.0");
    config.PropagateExceptions();
});

return await app.RunAsync(args);
