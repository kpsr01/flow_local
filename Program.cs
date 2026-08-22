using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

static void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");

Log("creating manager");
var cfg = new Configuration
{
    AppName = "FlowLocal",
    LogLevel = LogLevel.Debug,
    ModelCacheDir = @"C:\Users\harit\.foundry\cache\models"
};
await FoundryLocalManager.CreateAsync(cfg, NullLogger.Instance);
var manager = FoundryLocalManager.Instance;
Log("manager created");
Log("registering EPs");
await manager.DownloadAndRegisterEpsAsync();
Log("eps registered");
var catalog = await manager.GetCatalogAsync();
Log("catalog fetched");
var model = await catalog.GetModelAsync("nemotron-speech-streaming-en-0.6b")
    ?? throw new InvalidOperationException("alias missing");
Log($"model resolved: {model.Id}, cached={await model.IsCachedAsync()}");
Log("loading model");
await model.LoadAsync();
Log("model loaded");
var client = await model.GetAudioClientAsync();
Log("audio client ready");
Console.WriteLine("ALL GOOD - press enter to exit");
Console.ReadLine();
