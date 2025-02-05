using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jitbit.Utils;
using Microsoft.AspNetCore.Http.Features;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;
using Whisper.net;
using Timer = System.Timers.Timer;

namespace Onllama.Audios
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var configurationRoot = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddJsonFile("appsettings.json")
                .Build();

            var ttsEngines = new FastCache<string, OfflineTts>();

            var myWhisperFactory = WhisperFactory.FromPath(configurationRoot["WhisperModel"] ?? "whisper.bin");
            var myWhisperProcessor = myWhisperFactory.CreateBuilder()
                .WithLanguage("auto").Build();

            //new Timer(15000) {Enabled = true, AutoReset = true}.Elapsed += (_, _) =>
            //{
            //    ttsEngines.EvictExpired();
            //    GC.Collect(0);
            //};

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddAuthorization();
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                });
            });
            builder.Services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = 31457280; // 30 MB
            });
            var app = builder.Build();

            // Configure the HTTP request pipeline.
            app.UseCors();
            //app.UseHttpsRedirection();
            //app.UseAuthorization();

            app.Map("/v1/audio/transcriptions", async (HttpContext httpContext) =>
            {
                if (httpContext.Request.Method.ToUpper() != "POST")
                    return Results.Ok("Use Post");

                if (!httpContext.Request.HasFormContentType)
                    return Results.BadRequest("Invalid request format. Expected multipart/form-data.");

                var from = await httpContext.Request.ReadFormAsync();
                var isText = from.TryGetValue("response_format",out var format) && format == "text";
                var file = from.Files["file"];

                if (file == null || file.Length == 0)
                    return Results.BadRequest("No file uploaded.");

                var text = string.Empty;
                await foreach (var result in myWhisperProcessor.ProcessAsync(file.OpenReadStream()))
                    text += result.Text;

                return Results.Ok(isText ? text : new {file.FileName, file.Length, text});
            });

            app.Map("/v1/audio/speech", async (HttpContext httpContext) =>
            {
                var input = "什么都没有输入哦, Nothing in input";
                var voice = 0;
                var speed = 1.0f;
                var model = "kokoro-en-tts";

                if (httpContext.Request.Method.ToUpper() == "POST")
                {
                    var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
                    var str = await reader.ReadToEndAsync();
                    Console.WriteLine(str);
                    var json = JsonNode.Parse(str);
                    input = json?["input"]?.ToString();
                    voice = int.TryParse(json?["voice"]?.ToString(), out var vid) ? vid : 0;
                    speed = float.TryParse(json?["speed"]?.ToString(), out var fs) ? fs : 1.0f;
                    model = json?["model"]?.ToString();

                    if (string.IsNullOrWhiteSpace(model) || File.Exists(model + ".json")) model = configurationRoot["SherpaTtsConfig"];
                }
                else if (httpContext.Request.Method.ToUpper() == "GET" &&
                         httpContext.Request.Query.ContainsKey("input"))
                {
                    input = httpContext.Request.Query["input"];
                }

                Console.WriteLine("input:" + input);

                //if (File.Exists(ttsConfigPath))
                //    config = JsonSerializer.Deserialize<OfflineTtsConfig>(await File.ReadAllTextAsync(ttsConfigPath),
                //        new JsonSerializerOptions {IncludeFields = true});
                //else
                //    return Results.BadRequest("Cannot find tts configuration file " + ttsConfigPath);

                //Console.WriteLine(JsonSerializer.Serialize(config, new JsonSerializerOptions { IncludeFields = true }));

                #region TTSConfig

                //config.Model.Vits.Model = "./vits-icefall-zh-aishell3/model.onnx";
                //config.Model.Vits.Lexicon = "./vits-icefall-zh-aishell3/lexicon.txt";
                //config.Model.Vits.Tokens = "./vits-icefall-zh-aishell3/tokens.txt";
                //config.Model.Vits.DataDir = options.DataDir;
                //config.Model.Vits.DictDir = options.DictDir;
                //config.Model.Vits.NoiseScale = options.NoiseScale;
                //config.Model.Vits.NoiseScaleW = options.NoiseScaleW;
                //config.Model.Vits.LengthScale = options.LengthScale;

                //config.Model.Matcha.AcousticModel = "./matcha-icefall-zh-baker/model-steps-3.onnx";
                //config.Model.Matcha.Vocoder = "./hifigan_v2.onnx";
                //config.Model.Matcha.Lexicon = "./matcha-icefall-zh-baker/lexicon.txt";
                //config.Model.Matcha.Tokens = "./matcha-icefall-zh-baker/tokens.txt";
                ////config.Model.Matcha.DataDir = "./matcha-icefall-en_US-ljspeech/espeak-ng-data";
                //config.Model.Matcha.DictDir = "./matcha-icefall-zh-baker/dict";
                //config.Model.Matcha.NoiseScale = 0.667F;
                //config.Model.Matcha.LengthScale = 1;
                //config.RuleFsts = "./matcha-icefall-zh-baker/phone.fst,./matcha-icefall-zh-baker/date.fst,./matcha-icefall-zh-baker/number.fst";
                //config.RuleFars = "./vits-icefall-zh-aishell3/rule.far";

                //config.Model.NumThreads = 4;
                //config.Model.Debug = 1;
                //config.Model.Provider = "cpu";
                //config.MaxNumSentences = 1;

                #endregion


                var ttsEngine = ttsEngines.TryGet(model, out var tts)
                    ? tts
                    : new OfflineTts(JsonSerializer.Deserialize<OfflineTtsConfig>(
                        await File.ReadAllTextAsync(model + (model.EndsWith(".json") ? string.Empty : ".json")),
                        new JsonSerializerOptions {IncludeFields = true}));

                ttsEngines.AddOrUpdate(model, ttsEngine, TimeSpan.FromMinutes(25));

                var audio = ttsEngine.Generate(input, speed, voice);
                var file = $"./{Guid.NewGuid()}.wav";
                var ok = audio.SaveToWaveFile(file);

                if (!ok) return Results.StatusCode(500);
                httpContext.Response.Headers.ContentType = "audio/wav";
                await httpContext.Response.SendFileAsync(file);
                if(File.Exists(file)) File.Delete(file);
                return Results.Empty;
            });

            app.Run();
        }
    }
}
