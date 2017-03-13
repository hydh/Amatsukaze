﻿using Codeplex.Data;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace AmatsukazeServer
{
    public class ClientManager : IUserClient
    {
        private class Client
        {
            private ClientManager manager;
            private TcpClient client;
            private NetworkStream stream;

            public Client(TcpClient client, ClientManager manager)
            {
                this.manager = manager;
                this.client = client;
                this.stream = client.GetStream();
            }

            public async Task Start()
            {
                try
                {
                    while (true)
                    {
                        var rpc = await RPCTypes.Deserialize(stream);
                        manager.OnRequestReceived(this, rpc.id, rpc.arg);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    Close();
                }
                manager.OnClientClosed(this);
            }

            public void Close()
            {
                if (client != null)
                {
                    client.Close();
                    client = null;
                }
            }

            public NetworkStream GetStream()
            {
                return stream;
            }
        }

        private TcpListener listener;
        private bool finished = false;

        private List<Client> clientList = new List<Client>();
        private List<Task> receiveTask = new List<Task>();

        public void Finish()
        {
            if (listener != null)
            {
                listener.Stop();
                listener = null;

                foreach (var client in clientList)
                {
                    client.Close();
                }
            }
        }

        public async Task Listen()
        {
            int port = int.Parse(ConfigurationManager.AppSettings["Port"]);
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            try
            {
                while (true)
                {
                    var client = new Client(await listener.AcceptTcpClientAsync(), this);
                    ServerMain.CheckThread();
                    receiveTask.Add(client.Start());
                    clientList.Add(client);
                }
            }
            catch (Exception e)
            {
                if (finished == false)
                {
                    Console.WriteLine(e.Message);
                }
            }
        }

        private async Task Send(RPCMethodId id, object obj)
        {
            byte[] bytes = RPCTypes.Serialize(id, obj);
            foreach (var client in clientList.ToArray())
            {
                await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
                ServerMain.CheckThread();
            }
        }

        private IEncodeServer server;

        public ClientManager(IEncodeServer server)
        {
            this.server = server;
        }

        private void OnRequestReceived(Client client, RPCMethodId methodId, object arg)
        {
            switch (methodId)
            {
                case RPCMethodId.SetSetting:
                    server.SetSetting((Setting)arg);
                    break;
                case RPCMethodId.AddQueue:
                    server.AddQueue((string)arg);
                    break;
                case RPCMethodId.RemoveQueue:
                    server.RemoveQueue((string)arg);
                    break;
                case RPCMethodId.PauseEncode:
                    server.PauseEncode((bool)arg);
                    break;
                case RPCMethodId.RequestSetting:
                    server.RequestSetting();
                    break;
                case RPCMethodId.RequestQueue:
                    server.RequestQueue();
                    break;
                case RPCMethodId.RequestLog:
                    server.RequestLog();
                    break;
                case RPCMethodId.RequestConsole:
                    server.RequestConsole();
                    break;
                case RPCMethodId.RequestLogFile:
                    server.RequestLogFile((LogItem)arg);
                    break;
                case RPCMethodId.RequestState:
                    server.RequestState();
                    break;
            }
        }

        private void OnClientClosed(Client client)
        {
            int index = clientList.IndexOf(client);
            if (index >= 0)
            {
                receiveTask.RemoveAt(index);
                clientList.RemoveAt(index);
            }
        }

        #region IUserClient
        public Task OnSetting(Setting data)
        {
            return Send(RPCMethodId.OnSetting, data);
        }

        public Task OnQueueData(QueueData data)
        {
            return Send(RPCMethodId.OnQueueData, data);
        }

        public Task OnQueueUpdate(QueueUpdate update)
        {
            return Send(RPCMethodId.OnQueueUpdate, update);
        }

        public Task OnLogData(LogData data)
        {
            return Send(RPCMethodId.OnLogData, data);
        }

        public Task OnLogUpdate(LogItem newLog)
        {
            return Send(RPCMethodId.OnLogUpdate, newLog);
        }

        public Task OnConsole(string str)
        {
            return Send(RPCMethodId.OnConsole, str);
        }

        public Task OnConsoleUpdate(string str)
        {
            return Send(RPCMethodId.OnConsoleUpdate, str);
        }

        public Task OnLogFile(string str)
        {
            return Send(RPCMethodId.OnLogFile, str);
        }

        public Task OnState(State state)
        {
            return Send(RPCMethodId.OnState, state);
        }

        public Task OnOperationResult(string result)
        {
            return Send(RPCMethodId.OnOperationResult, result);
        }
        #endregion
    }

    public class EncodeServer : IEncodeServer
    {
        private class TargetDirectory
        {
            public string DirPath { get; private set; }
            public List<string> TsFiles {get;private set;}

            public TargetDirectory(string dirPath)
            {
                DirPath = dirPath;
                TsFiles = Directory.GetFiles(DirPath, "*.ts").ToList();
            }
        }

        [DataContract]
        private class AppData : IExtensibleDataObject
        {
            [DataMember]
            public Setting setting;

            public ExtensionDataObject ExtensionData { get; set; }
        }

        private static string LOG_FILE = "log.xml";
        private static string LOG_DIR = "logs";

        private ClientManager clientManager;
        public Task ServerTask { get; private set; }
        private AppData appData;

        private List<TargetDirectory> queue = new List<TargetDirectory>();
        private LogData log;
        private Queue<string> consoleStrings = new Queue<string>();

        private StreamWriter logWriter;

        private bool encodePaused = false;
        private bool nowEncoding = false;

        public EncodeServer()
        {
            LoadAppData();
            clientManager = new ClientManager(this);
            ServerTask = clientManager.Listen();
            ReadLog();
        }

        #region Path
        private string GetAmatsukzeCLIPath()
        {
            return Path.Combine(Path.GetDirectoryName(
                typeof(EncodeServer).Assembly.Location), "AmatsukazeCLI.exe");
        }

        private string GetSettingFilePath()
        {
            return "AmatsukazeServer.xml";
        }

        private string GetLogFilePath(DateTime start)
        {
            return LOG_DIR + "\\" + start.ToString("yyyy-MM-dd_HHmmss.SSS") + ".txt";
        }

        private string ReadLogFIle(DateTime start)
        {
            return File.ReadAllText(GetLogFilePath(start));
        }
        #endregion

        public void Finish()
        {
            if (clientManager != null)
            {
                clientManager.Finish();
                clientManager = null;
            }
        }

        private void LoadAppData()
        {
            if(File.Exists(GetSettingFilePath()) == false)
            {
                appData = new AppData() {
                    setting = new Setting()
                };
                return;
            }
            using (FileStream fs = new FileStream(GetSettingFilePath(), FileMode.Open))
            {
                var s = new DataContractSerializer(typeof(AppData));
                appData = (AppData)s.ReadObject(fs);
                if (appData.setting == null)
                {
                    appData.setting = new Setting();
                }
            }
        }

        private void SaveAppData()
        {
            using (FileStream fs = new FileStream(GetSettingFilePath(), FileMode.Create))
            {
                var s = new DataContractSerializer(typeof(AppData));
                s.WriteObject(fs, appData);
            }
        }

        private void ReadLog()
        {
            if (File.Exists(LOG_FILE) == false)
            {
                log = new LogData()
                {
                    Items = new List<LogItem>()
                };
                return;
            }
            try
            {
                using (FileStream fs = new FileStream(LOG_FILE, FileMode.Open))
                {
                    var s = new DataContractSerializer(typeof(LogData));
                    log = (LogData)s.ReadObject(fs);
                    if (log.Items == null)
                    {
                        log.Items = new List<LogItem>();
                    }
                }
            }
            catch (IOException e)
            {
                Console.WriteLine("ログファイルの読み込みに失敗: " + e.Message);
            }
        }

        private void WriteLog()
        {
            try
            {
                using (FileStream fs = new FileStream(LOG_FILE, FileMode.Create))
                {
                    var s = new DataContractSerializer(typeof(LogData));
                    s.WriteObject(fs, log);
                }
            }
            catch (IOException e)
            {
                Console.WriteLine("ログファイル書き込み失敗: " + e.Message);
            }
        }

        private string GetEncoderPath()
        {
            if (appData.setting.EncoderName == "x264")
            {
                return appData.setting.X264Path;
            }
            else if(appData.setting.EncoderName == "x265")
            {
                return appData.setting.X265Path;
            }
            else if(appData.setting.EncoderName == "QSVEnc")
            {
                return appData.setting.QSVEncPath;
            }
            else
            {
                throw new ArgumentException("エンコーダ名が認識できません");
            }
        }

        private string MakeAmatsukazeArgs(string src, string dst, out string json, out string log)
        {
            string workPath = string.IsNullOrEmpty(appData.setting.EncoderName)
                ? "./" : appData.setting.EncoderName;
            string encoderPath = GetEncoderPath();
            json = "amt-" + Process.GetCurrentProcess().Id.ToString() + ".json";
            log = Path.Combine(
                Path.GetDirectoryName(dst),
                Path.GetFileNameWithoutExtension(dst)) + "-enc.log";
            
            if (string.IsNullOrEmpty(encoderPath))
            {
                throw new ArgumentException("エンコーダパスが指定されていません");
            }
            if (string.IsNullOrEmpty(appData.setting.MuxerPath))
            {
                throw new ArgumentException("Muxerパスが指定されていません");
            }
            if (string.IsNullOrEmpty(appData.setting.TimelineEditorPath))
            {
                throw new ArgumentException("Timelineeditorパスが指定されていません");
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("-i \"")
                .Append(src)
                .Append("\" -o \"")
                .Append(dst)
                .Append("\" -w \"")
                .Append(appData.setting.WorkPath)
                .Append("\" -et ")
                .Append(appData.setting.EncoderName)
                .Append(" -e \"")
                .Append(encoderPath)
                .Append("\" -m \"")
                .Append(appData.setting.MuxerPath)
                .Append("\" -t \"")
                .Append(appData.setting.TimelineEditorPath)
                .Append("\" -j \"")
                .Append(Path.Combine(workPath, json))
                .Append("\"");

            if (string.IsNullOrEmpty(appData.setting.EncoderOption) == false)
            {
                sb.Append(" -eo \"")
                    .Append(appData.setting.EncoderOption)
                    .Append("\"");
            }

            return sb.ToString();
        }

        private async Task RedirectOut(StreamReader sr)
        {
            try
            {
                while (true)
                {
                    var str = await sr.ReadLineAsync();
                    if (logWriter != null)
                    {
                        logWriter.WriteLine(str);
                    }
                    if (consoleStrings.Count > 100)
                    {
                        consoleStrings.Dequeue();
                    }
                    consoleStrings.Enqueue(str);
                    await clientManager.OnConsoleUpdate(str);
                }
            }
            catch (Exception e)
            {
                Debug.Print("RedirectOut exception " + e.Message);
            }
        }

        private LogItem LogFromJson(string jsonpath, DateTime start, DateTime finish)
        {
            var json = DynamicJson.Parse(File.ReadAllText(jsonpath));
            var outpath = new List<string>();
            foreach (var path in json.outpath)
            {
                outpath.Add(path);
            }
            return new LogItem()
            {
                Success = true,
                SrcPath = json.srcpath,
                OutPath = outpath,
                SrcFileSize = json.srcfilesize,
                IntVideoFileSize = json.intvideofilesize,
                OutFileSize = json.outfilesize,
                SrcVideoDuration = json.srcduration,
                OutVideoDuration = json.outduration,
                EncodeStartDate = start,
                EncodeFinishDate = finish,
                AudioDiff = new AudioDiff()
                {
                    TotalSrcFrames = json.audiodiff.totalsrcframes,
                    TotalOutFrames = json.audiodiff.totaloutframes,
                    TotalOutUniqueFrames = json.audiodiff.totaloutuniqueframes,
                    NotIncludedPer = json.audiodiff.notincludedper,
                    AvgDiff = json.audiodiff.avgdiff,
                    MaxDiff = json.audiodiff.maxdiff,
                    MaxDiffPos = json.audiodiff.maxdiffpos
                }
            };
        }

        private LogItem FailLogItem(string reason, DateTime start, DateTime finish)
        {
            return new LogItem()
            {
                Success = false,
                Reason = reason,
                EncodeStartDate = start,
                EncodeFinishDate = finish
            };
        }

        private async Task ProcessDiretoryItem(TargetDirectory dir)
        {
            string succeeded = Path.Combine(dir.DirPath, "succeeded");
            string failed = Path.Combine(dir.DirPath, "failed");
            string encoded = Path.Combine(dir.DirPath, "encoded");
            Directory.CreateDirectory(succeeded);
            Directory.CreateDirectory(failed);
            Directory.CreateDirectory(encoded);

            foreach (var src in dir.TsFiles.ToArray())
            {
                dir.TsFiles.Remove(src);

                if (File.Exists(src) == false)
                {
                    DateTime now = DateTime.Now;
                    log.Items.Add(FailLogItem("入力ファイルが見つかりません", now, now));
                    WriteLog();
                    continue;
                }

                string dst = Path.Combine(encoded, Path.GetFileName(src));
                string json, logpath;
                string args = MakeAmatsukazeArgs(src, dst, out json, out logpath);
                string exename = Path.Combine(
                    Path.GetDirectoryName(this.GetType().Assembly.Location),
                    "\\AmatsukazeCLI.exe");
                
                Debug.Print("Args: " + exename + " " + args);

                DateTime start = DateTime.Now;

                Process p = new Process();
                var psi = new ProcessStartInfo(exename, args);
                p.StartInfo.FileName = exename;
                p.StartInfo.Arguments = args;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardInput = false;
                p.StartInfo.CreateNoWindow = true;

                using (logWriter = new StreamWriter(logpath))
                {
                    p.Start();

                    await Task.WhenAll(
                        RedirectOut(p.StandardOutput),
                        RedirectOut(p.StandardError),
                        Task.Run(() => p.WaitForExit()));
                }

                DateTime finish = DateTime.Now;

                // ログファイルを専用フォルダにコピー
                if (File.Exists(logpath))
                {
                    File.Copy(logpath, GetLogFilePath(start));
                }

                if (p.ExitCode == 0)
                {
                    // 成功
                    //File.Move(src, succeeded + "\\");

                    log.Items.Add(LogFromJson(json, start, finish));
                    WriteLog();
                }
                else
                {
                    // 失敗
                    //File.Move(src, failed + "\\");

                    // TODO:
                    throw new InvalidProgramException("エンコードに失敗した");
                }

                if (encodePaused)
                {
                    break;
                }
            }
        }

        private async Task StartEncode()
        {
            nowEncoding = true;

            while (queue.Count > 0)
            {
                await ProcessDiretoryItem(queue[0]);
                if (encodePaused)
                {
                    break;
                }
                queue.RemoveAt(0);
            }

            nowEncoding = false;
        }

        public Task SetSetting(Setting setting)
        {
            appData.setting = setting;
            SaveAppData();
            return clientManager.OnOperationResult("設定を更新しました");
        }

        public async Task AddQueue(string dirPath)
        {
            if (queue.Find(t => t.DirPath == dirPath) != null)
            {
                await clientManager.OnOperationResult(
                    "すでに同じパスが追加されています。パス:" + dirPath);
                return;
            }
            var target = new TargetDirectory(dirPath);
            if (target.TsFiles.Count == 0)
            {
                await clientManager.OnOperationResult(
                    "エンコード対象ファイルが見つかりません。パス:" + dirPath);
                return;
            }
            queue.Add(target);
            if (nowEncoding == false)
            {
                await StartEncode();
            }
        }

        public async Task RemoveQueue(string dirPath)
        {
            var target = queue.Find(t => t.DirPath == dirPath);
            if (target == null)
            {
                await clientManager.OnOperationResult(
                    "指定されたキューディレクトリが見つかりません。パス:" + dirPath);
                return;
            }
            queue.Remove(target);
        }

        public async Task PauseEncode(bool pause)
        {
            encodePaused = pause;
            if (encodePaused == false && nowEncoding == false)
            {
                await StartEncode();
            }
        }

        public Task RequestSetting()
        {
            return clientManager.OnSetting(appData.setting);
        }

        public Task RequestQueue()
        {
            QueueData data = new QueueData()
            {
                Items = queue.Select(item => new QueueItem()
                {
                    Path = item.DirPath,
                    MediaFiles = item.TsFiles
                }).ToList()
            };
            return clientManager.OnQueueData(data);
        }

        public Task RequestLog()
        {
            return clientManager.OnLogData(log);
        }

        public Task RequestConsole()
        {
            return clientManager.OnConsole(String.Join("\r\n", consoleStrings));
        }

        public Task RequestLogFile(LogItem item)
        {
            return clientManager.OnLogFile(ReadLogFIle(item.EncodeStartDate));
        }

        public Task RequestState()
        {
            var state = new State() {
                Pause = encodePaused
            };
            return clientManager.OnState(state);
        }
    }
}