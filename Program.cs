using Microsoft.AspNetCore.Http.Features;
using Whisper.net;

namespace Onllama.Audios
{
    public class Program
    {
        private static WhisperFactory MyWhisperFactory = WhisperFactory.FromPath("ggml-base-q5_1.bin");
        private static WhisperProcessor MyWhisperProcessor = MyWhisperFactory.CreateBuilder()
            .WithLanguage("auto").Build();

        public static void Main(string[] args)
        {
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
                await foreach (var result in MyWhisperProcessor.ProcessAsync(file.OpenReadStream()))
                    text += result.Text;

                return Results.Ok(isText ? text : new {file.FileName, file.Length, text});
            });

            app.Run();
        }
    }
}
