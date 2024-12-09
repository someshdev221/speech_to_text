using System;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using NAudio.Wave;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech;

namespace SpeechText
{
    public class SpeechFunction
    {

        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly AzureSpeechSettings SpeechSettings = new AzureSpeechSettings
        {
            SubscriptionKey = Environment.GetEnvironmentVariable("AzureSpeechSubscriptionKey"),
            Region = Environment.GetEnvironmentVariable("AzureSpeechRegion")
        };

        [FunctionName("RecognizeSpeechFromBlobUrl")]
        public static async Task<IActionResult> RecognizeSpeechFromBlobUrl(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "recognize")] HttpRequest req,
        ILogger log)
        {
            log.LogInformation("Speech recognition function triggered.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<SpeechRequest>(requestBody);

            if (request == null || string.IsNullOrEmpty(request.BlobFileUrl))
                return new BadRequestObjectResult("Blob file URL is required.");

            string tempFilePath = null;
            List<string> chunkPaths = null;

            try
            {
                // Step 1: Download and Convert the File
                tempFilePath = await ConvertToSupportedFormatAsync(request.BlobFileUrl);

                if (string.IsNullOrEmpty(tempFilePath))
                    return new NotFoundObjectResult("Unable to process the audio file.");

                // Step 2: Split Audio File into Chunks
                chunkPaths = await SplitAudioFile(tempFilePath, 35); // 35 seconds per chunk
                var recognitionResults = new List<string>();

                var tasks = chunkPaths.Select(chunkPath => RecognizeChunkAsync(chunkPath)).ToList();
                var recognitionTimeout = Task.Delay(30000); // 30-second timeout
                var allRecognitionTasks = Task.WhenAll(tasks);

                // Wait for either the recognition to complete or timeout
                if (await Task.WhenAny(allRecognitionTasks, recognitionTimeout) == recognitionTimeout)
                {
                    log.LogWarning("Recognition timed out.");
                    return new StatusCodeResult(408); // Request Timeout
                }

                // Aggregate results
                foreach (var task in tasks)
                {
                    recognitionResults.AddRange(await task);
                }

                return new OkObjectResult(new
                {
                    Message = "Speech recognition completed.",
                    Results = recognitionResults
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "An error occurred during speech recognition.");
                return new StatusCodeResult(500); // Internal Server Error
            }
            finally
            {
                // Cleanup temporary files
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                    File.Delete(tempFilePath);

                if (chunkPaths != null)
                {
                    foreach (var chunkPath in chunkPaths)
                    {
                        if (File.Exists(chunkPath))
                            File.Delete(chunkPath);
                    }
                }
            }
        }

        private static async Task<string> ConvertToSupportedFormatAsync(string blobFileUrl)
        {
            try
            {
                var response = await HttpClient.GetAsync(blobFileUrl);
                if (!response.IsSuccessStatusCode)
                    return null;

                var webmFilePath = Path.GetTempFileName();
                await using (var fileStream = new FileStream(webmFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fileStream);
                }

                var wavFilePath = Path.ChangeExtension(webmFilePath, ".wav");
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = @"C:\ffmpeg\bin\ffmpeg.exe",
                        Arguments = $"-i \"{webmFilePath}\" -ar 16000 -ac 1 \"{wavFilePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                    return null;

                File.Delete(webmFilePath);

                return wavFilePath;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<List<string>> SplitAudioFile(string filePath, int chunkDurationSeconds)
        {
            var chunkPaths = new List<string>();
            var outputDirectory = Path.Combine(Path.GetTempPath(), "chunks");

            Directory.CreateDirectory(outputDirectory);

            using var reader = new WaveFileReader(filePath);
            int bytesPerSecond = reader.WaveFormat.AverageBytesPerSecond;
            int chunkSize = bytesPerSecond * chunkDurationSeconds;

            byte[] buffer = new byte[chunkSize];
            int bytesRead;
            int chunkNumber = 1;

            while ((bytesRead = reader.Read(buffer, 0, chunkSize)) > 0)
            {
                string chunkPath = Path.Combine(outputDirectory, $"chunk_{chunkNumber++}.wav");

                using var writer = new WaveFileWriter(chunkPath, reader.WaveFormat);
                writer.Write(buffer, 0, bytesRead);

                chunkPaths.Add(chunkPath);
            }

            return chunkPaths;
        }

        private static async Task<List<string>> RecognizeChunkAsync(string chunkFilePath)
        {
            var recognitionResults = new List<string>();

            var speechConfig = SpeechConfig.FromSubscription(SpeechSettings.SubscriptionKey, SpeechSettings.Region);
            speechConfig.SpeechRecognitionLanguage = "en-US";
            speechConfig.SetProfanity(ProfanityOption.Masked);

            using var audioInput = AudioConfig.FromWavFileInput(chunkFilePath);
            using var recognizer = new SpeechRecognizer(speechConfig, audioInput);

            var tcs = new TaskCompletionSource<bool>();

            recognizer.Recognized += (sender, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    recognitionResults.Add(e.Result.Text);
                }
            };

            recognizer.Canceled += (sender, e) =>
            {
                tcs.TrySetResult(true);
            };

            recognizer.SessionStopped += (sender, e) =>
            {
                tcs.TrySetResult(true);
            };

            await recognizer.StartContinuousRecognitionAsync();
            await tcs.Task;
            await recognizer.StopContinuousRecognitionAsync();

            return recognitionResults;
        }

        //public void Run([QueueTrigger("myqueue-items", Connection = "")]string myQueueItem, ILogger log)
        //{
        //    log.LogInformation($"C# Queue trigger function processed: {myQueueItem}");
        //}
    }

    public class SpeechRequest
    {
        public string BlobFileUrl { get; set; }
    }

    public class AzureSpeechSettings
    {
        public string SubscriptionKey { get; set; }
        public string Region { get; set; }
    }
}
