using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jitbit.Utils;
using Microsoft.AspNetCore.Http.Features;
using SherpaOnnx;
using Whisper.net;
using FFMpegCore;
using FFMpegCore.Pipes;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using McMaster.Extensions.CommandLineUtils;

namespace Onllama.Audios
{
    [Command(Name = "oudios"),
     Subcommand(typeof(PullCommand), typeof(ServeCommand))]
    class Program
    {
        public static string BasePath = ".";
        static int Main(string[] args) => CommandLineApplication.Execute<Program>(args);

        private void OnExecute(CommandLineApplication app)
        {
            Console.WriteLine(""""
                                         ,  ,
                                        (\ "\
                                        ,--;.)._
                                       ).,-._ . ""-,_
                                      /.'".- " 8 o . ";_  Onllama.Audios / Oudios Lite                           
                                      `L_ ,-)) o . 8.o .""-.---...,,--------.._   _"";
                                       """  ")) 8 . . 8 . 8   8  8  8  8. 8 8 ._""._;
                                             ";. .8 .8  .8  8  8  8  8 . 8. 8 .".""
                                                ;.. 8 ; .  8. 8  8  8 . } 8 . 8 :
                                                 ;.. 8 ; 8. 8  8  8  8 (  . 8 . :
                                                   ;. 8 \ .   .......;;;  8 . 8 :
                                                    ;o  ;"\\\\```````( o(  8   .;
                                                    : o:  ;           :. : . 8 (
                                                    :o ; ;             "; ";. o :
                                                    ; o; ;               "; ;";..\
                                      (c) Milkey T. ;.; .:                )./  ;. ;
                                          2025     _).< .;              _;./  _;./
                                                 ;"__/--"             ((__7  ((_J
                                      """");
            Console.WriteLine();
            if (!Directory.Exists("models")) app.ShowHelp();
            else RunServer();
        }

        [Command("pull", Description = "拉取指定配置文件和模型")]
        class PullCommand
        {
            [Argument(0, Description = "配置文件路径")]
            public string FileUrl { get; }

            private void OnExecute()
            {
                if (!Directory.Exists("models")) Directory.CreateDirectory("models");
                var jsonUrl = new Uri(FileUrl);
                Task.Run(async () =>
                {
                    try
                    {
                        var json = JsonNode.Parse(await new HttpClient().GetStringAsync(jsonUrl));
                        await File.WriteAllTextAsync(Path.Combine(BasePath, "manifests", jsonUrl.Segments.Last()),
                            json.ToJsonString());
                        foreach (var item in json["Files"].AsArray())
                        {
                            if (item.ToString().EndsWith(".tar.bz2"))
                            {
                                using var response = await new HttpClient().GetAsync(item.ToString());
                                await using var bzip2Stream = new BZip2InputStream(await response.Content.ReadAsStreamAsync());
                                await using var tarInputStream = new TarInputStream(bzip2Stream, Encoding.UTF8);
                                while (tarInputStream.GetNextEntry() is { } entry)
                                {
                                    if (entry.IsDirectory) continue;
                                    var entryPath = Path.Combine(BasePath, "models", entry.Name);
                                    var entryDir = Path.GetDirectoryName(entryPath);
                                    if (!Directory.Exists(entryDir)) Directory.CreateDirectory(entryDir);
                                    await using var entryStream = File.Create(entryPath);
                                    tarInputStream.CopyEntryContents(entryStream);
                                }
                            }
                            else if (item.ToString().EndsWith(".onnx"))
                            {
                                await File.WriteAllBytesAsync(new Uri(item.ToString()).Segments.Last(),
                                    await (await new HttpClient().GetAsync(item.ToString())).Content
                                        .ReadAsByteArrayAsync());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                }).Wait();
            }
        }

        [Command("serve", Description = "启动服务器")]
        class ServeCommand
        {
            [Option("--config|-c", Description = "服务器配置文件路径")]
            public string Config { get; }

            public void OnExecute() => RunServer(string.IsNullOrWhiteSpace(Config) ? "appsettings.json" : Config);
        }

        public static void RunServer(string config = "appsettings.json")
        {
            var configurationRoot = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddJsonFile(config)
                .Build();

            var ttsEngines = new FastCache<string, OfflineTts>();

            var myWhisperProcessor = WhisperFactory
                .FromPath(configurationRoot["WhisperModel"] ?? Path.Combine(BasePath, "models", "whisper.bin"))
                .CreateBuilder()
                .WithLanguage("auto").Build();

            //new Timer(15000) {Enabled = true, AutoReset = true}.Elapsed += (_, _) =>
            //{
            //    ttsEngines.EvictExpired();
            //    GC.Collect(0);
            //};

            var builder = WebApplication.CreateBuilder();
            //var builder = WebApplication.CreateBuilder(args);

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
                var isText = from.TryGetValue("response_format", out var format) && format == "text";
                var file = from.Files["file"];

                if (file == null || file.Length == 0)
                    return Results.BadRequest("No file uploaded.");
                var text = string.Empty;

                MemoryStream memoryStream = new();
                await FFMpegArguments
                    .FromPipeInput(new StreamPipeSource(file.OpenReadStream()))
                    .OutputToPipe(new StreamPipeSink(memoryStream), options => options
                        .WithAudioSamplingRate(16000)
                        .ForceFormat("wav")
                    ).ProcessAsynchronously();
                memoryStream.Position = 0;

                await foreach (var result in myWhisperProcessor.ProcessAsync(memoryStream))
                    text += result.Text;
                return Results.Ok(isText ? text : new { file.FileName, file.Length, text });
            });

            app.Map("/v1/audio/speech", async (HttpContext httpContext) =>
            {
                var input = "什么都没有输入, Nothing in input";
                var voice = 0;
                var speed = 1.1f;
                var model = "kokoro-multi-lang-tts";

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
                }
                else if (httpContext.Request.Method.ToUpper() == "GET" &&
                         httpContext.Request.Query.ContainsKey("input"))
                {
                    input = httpContext.Request.Query["input"];
                }

                if (string.IsNullOrWhiteSpace(model) ||
                    !File.Exists(Path.Combine(BasePath, "manifests", model + ".json")))
                    model = configurationRoot["SherpaTtsConfig"];
                else
                    model = Path.Combine(BasePath, "manifests", model + ".json");

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
                        await File.ReadAllTextAsync(model),
                        new JsonSerializerOptions { IncludeFields = true }));

                ttsEngines.AddOrUpdate(model, ttsEngine, TimeSpan.FromMinutes(25));

                var audio = ttsEngine.Generate(input, speed, voice);
                var file = $"./{Guid.NewGuid()}.wav";
                var ok = audio.SaveToWaveFile(file);

                if (!ok) return Results.StatusCode(500);
                httpContext.Response.Headers.ContentType = "audio/wav";
                await httpContext.Response.SendFileAsync(file);
                if (File.Exists(file)) File.Delete(file);
                return Results.Empty;
            });

            app.Run();
        }
    }


}
