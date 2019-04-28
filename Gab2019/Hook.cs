using Linebot.Core.Mn;
using Linebot.Core.Mn.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Gab2019
{
    public static class Hook
    {
        ///TODO: using HttpClient like a singleton instance
        private static HttpClient _client = HttpClientFactory.Create();

        [FunctionName("Hook")]
        public static async Task<IActionResult> Run (
#if DEBUG
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
#else
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
#endif
            ExecutionContext context,
            ILogger log)
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            log.LogInformation($"{body}");

            var xline = GetSignature(req, log);
            var signatureVerify = new Signature().Verify(body, xline, out string bodySignature);
            if (!signatureVerify)
            {
                log.LogError($"Body Signature: {bodySignature}\nX-Line-Signature: {xline}\n簽章驗證無效！");
                return new OkObjectResult(new { });
            }

            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("options.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var settings = config.GetSection("Settings").Get<Settings>();

            //rewrite body as test things
            //body = "{\"events\":[{\"type\":\"message\",\"replyToken\":\"9b3b1ebbae04424795449febea80ebb1\",\"source\":{\"userId\":\"U9b91b518d6888d33993f22..........\",\"type\":\"user\"},\"timestamp\":1555777816998,\"message\":{\"type\":\"text\",\"id\":\"9727800348111\",\"text\":\"line://app/1566998925-rZDo1ZXB \"}}],\"destination\":\"U06d5e57efb11eea8973b89..........\"}";
            //body = "{\"events\":[{\"type\":\"message\",\"replyToken\":\"34e78522e9fc4017a856b2b48139ff1e\",\"source\":{\"userId\":\"U9b91b518d6888d33993f22..........\",\"type\":\"user\"},\"timestamp\":1556126154438,\"message\":{\"type\":\"image\",\"id\":\"9750780588237\",\"contentProvider\":{\"type\":\"line\"}}}],\"destination\":\"U06d5e57efb11eea8973b89..........\"}";

            #region DO NOT use HttpClien like this. very very bad for performance.
            //using (var httpClient = new HttpClient())
            //{
            //    var par = new Parser(body);
            //    par.Determine();

            //    var me = (TextMessageEventRawData)par.MessageEvent;
            //    var msg = me.events.FirstOrDefault()?.message;
            //    var remsg = $"{msg.text}";

            //    var e = new Engine(httpClient, me.destination);
            //    var msgs = new IMessageObject[] {
            //        new TextMessageObject {
            //            text = remsg
            //        }
            //    };
            //    await e.SendMessages(msgs);
            //}
            #endregion

            var par = new Parser(body);
            par.Determine();

            var type = par.SourceMsgType.HasValue
                    ? par.SourceMsgType.Value.ToString()
                    : string.Empty;
            switch (type)
            {
                case "text": //MessageTypeEnum.text.ToString():
                    await GoByTextMessage(par);
                    break;

                case "image":
                    await GoByImageMessage(par, settings);
                    break;

                default:
                    break;
            }
            return new OkObjectResult(new { });
        }

        #region settings

        class Settings
        {
            public BlobSettings BlobSettings { get; set; }
            public ComputerVision ComputerVision { get; set; }
        }

        class BlobSettings
        {
            public string StorageConnectionString { get; set; }
            public string ContainerName { get; set; }
            public string BlobEndpoint { get; set; }
        }

        class ComputerVision
        {
            public string OcrApi { get; set; }
            public string ApiKey { get; set; }
        }

        #endregion

        #region process

        private static string GetSignature(HttpRequest req, ILogger log)
        {
            req.Headers.TryGetValue("X-Line-Signature", out var xLineSignature);
            var xline = xLineSignature.FirstOrDefault() ?? string.Empty;
            log.LogInformation($"Signature: {xline}");
            return xline;
        }

        private static async Task GoByTextMessage(Parser par)
        {
            var pme = (TextMessageEventRawData)par.MessageEvent;
            var firstEvent = pme.events.FirstOrDefault();
            var msg = firstEvent?.message;

            // 你說什麼，我回什麼！使用 replyToken 回應，這個 token 是短效權杖，只有 10 秒的生命週期
            var remsg = $"你說什麼，我回什麼：\n{msg.text}\nat {DateTime.Now.ToString()}";

            var e = new Engine(firstEvent?.replyToken, true);
            var msgs = new IMessageObject[] {
                        new TextMessageObject {
                            text = remsg
                        }
                    };
            await e.QuickSend(msgs);
        }

        private static async Task GoByImageMessage(Parser par, Settings settings)
        {
            var pme = par.MessageEvent as ImageMessageEventRawData;
            var firstEvt = pme.events.FirstOrDefault();
            var msg = firstEvt.message;
            var userId = firstEvt?.source.userId;

            var imageId = msg.id; // 可由 Get Content Api 取得圖片的二進位內容。
                                   // PS：上傳到 Line 的圖片／檔案，並不會長時間保存，如果有留存必要，請一定要額外儲存
                                   // PS2：上傳的圖片，並不會記錄檔名，回存時，要如何指定 file extension name？

            var e = new Engine();
            var contentObject = await e.GetContent(imageId);

            var filename = $"{Guid.NewGuid().ToString()}.{DetermineImageExteionName(contentObject.Item2)}";

            var fileEndpoint = $"{settings?.BlobSettings.BlobEndpoint}/{settings?.BlobSettings?.ContainerName}/{filename}";
            await SaveFileToBlob(
                settings?.BlobSettings?.StorageConnectionString,
                contentObject.Item1,
                settings?.BlobSettings?.ContainerName,
                filename,
                contentObject.Item2);

            e.DestinationUserId = userId;

            var msgs = new IMessageObject[] {
                        new TextMessageObject {
                            text = $"您的圗片存在這裡：\n{fileEndpoint}"
                        }
                    };
            await e.SendMessages(msgs);

            // 接著，繼續處理。透過 Computer Vision API：OCR，分析剛剛上傳的圖片
            await DoComputerVisionOCR(settings, fileEndpoint, e);
            
        }

        static string DetermineImageExteionName(string contentType)
        {
            var rest = "unknowfile";
            switch (contentType)
            {
                case "image/jpeg":
                    rest = "jpg";
                    break;


                case "image/png":
                    rest = "png";
                    break;

                case "image/gif":
                    rest = "gif";
                    break;

                case "image/tiff":
                    rest = "tiff";
                    break;

                case "image/vnd.wap.wbmp":
                    rest = "wbmp";
                    break;

                case "image/x-icon":
                    rest = "ico";
                    break;
                case "image/x-jng":
                    rest = "jng";
                    break;
                case "image/x-ms-bmp":
                    rest = "bmp";
                    break;
                case "image/svg+xml":
                    rest = "svg";
                    break;
                case "image/webp":
                    rest = "webp";
                    break;
                default:
                    break;
            }
            return rest;
        }

        private static async Task DoComputerVisionOCR(Settings settings, string fileEndpoint, Engine e)
        {
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", settings?.ComputerVision.ApiKey);

            var ocrApi = settings?.ComputerVision?.OcrApi;
            var parameters = "?language=en&detectOrientation=true";
            var post = await _client.PostAsJsonAsync(
                $"{ocrApi}{parameters}",
                new
                {
                    url = fileEndpoint
                });

            var ret = await post.Content.ReadAsStringAsync();
            var words = AnalysisOcrResult(ret);
            await e.SendMessages(new IMessageObject[] {
                new TextMessageObject{
                    text = $"上述的圖片中，我看到了\n{words}"
                }
            });
        }
        #endregion

        #region ocr analisys

        sealed class OcrResult
        {
            public string Language { get; set; }
            public double TextAngle { get; set; }
            public string Orientation { get; set; }
            public IEnumerable<Region> Regions { get; set; }
        }

        sealed class Region
        {
            public string BoundingBox { get; set; }
            public IEnumerable<Line> Lines { get; set; }
        }

        sealed class Line
        {
            public string BoundingBox { get; set; }
            public IEnumerable<Word> Words { get; set; }
        }

        sealed class Word
        {
            public string BoundingBox { get; set; }
            public string Text { get; set; }
        }

        private static string AnalysisOcrResult(string jsonResult)
        {
            var recognizedWords = string.Empty;
            var ocrResult = JsonConvert.DeserializeObject<OcrResult>(jsonResult);
            foreach (var region in ocrResult.Regions)
            {
                foreach (var line in region.Lines)
                {
                    foreach (var word in line.Words)
                    {
                        recognizedWords += word.Text + "\n";
                    }
                }
            }
            return recognizedWords;
        }
#endregion
        #region blob ref
        private static async Task SaveFileToBlob(
            string storageConnectionString,
            Stream stream,
            string containerName,
            string fileName,
            string contentType)
        {
            var blobClient = GetCloudBlobClient(storageConnectionString);
            var container = GetContainer(blobClient, containerName);
            var blobName = $"{fileName}"; //$"{this.SubFolder}/{this.FileName}";
            var blockBlob = GetCloudBlockBlob(container, blobName, contentType);

            await blockBlob.UploadFromStreamAsync(stream);
        }

        private static CloudBlobClient GetCloudBlobClient(string storageConnectionString)
        {
            CloudStorageAccount.TryParse(storageConnectionString, out CloudStorageAccount cloudStorageAccount);
            var blobClient = cloudStorageAccount.CreateCloudBlobClient();
            return blobClient;
        }

        private static CloudBlobContainer GetContainer(CloudBlobClient blobClient, string containerName)
        {
            var container = blobClient.GetContainerReference(containerName);
            container.CreateIfNotExistsAsync();

            container.SetPermissionsAsync(new BlobContainerPermissions
            {
                PublicAccess = BlobContainerPublicAccessType.Blob
            });

            return container;
        }

        private static CloudBlockBlob GetCloudBlockBlob(CloudBlobContainer container, string blobName, string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                // 不指定 content type 時，預設為 application/octet-stream，若開啟 url 時，將造成檔案直接下載
                return container.GetBlockBlobReference(blobName);
            }
            else
            {
                var blockBlob = container.GetBlockBlobReference(blobName);
                blockBlob.Properties.ContentType = contentType;
                blockBlob.SetPropertiesAsync();
                return blockBlob;
            }
        }
        #endregion
    }
}