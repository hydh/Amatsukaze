﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Amatsukaze.Server
{
    public interface IEncodeServer
    {
        // 操作系
        Task SetSetting(Setting setting);
        Task AddQueue(AddQueueDirectory dir);
        Task RemoveQueue(int id);
        Task ChangeItem(ChangeItemData data);
        Task PauseEncode(bool pause);

        Task SetServiceSetting(ServiceSettingUpdate update);
        Task AddDrcsMap(DrcsImage drcsMap);

        // 情報取得系
        Task RequestSetting();
        Task RequestQueue();
        Task RequestLog();
        Task RequestConsole();
        Task RequestLogFile(LogItem item);
        Task RequestState();
        Task RequestFreeSpace();

        Task RequestServiceSetting();
        Task RequestLogoData(string fileName);
        Task RequestDrcsImages();

        void Finish();
    }

    public interface IUserClient
    {
        Task OnSetting(Setting setting);
        Task OnQueueData(QueueData data);
        Task OnQueueUpdate(QueueUpdate update);
        Task OnLogData(LogData data);
        Task OnLogUpdate(LogItem newLog);
        Task OnConsole(ConsoleData str);
        Task OnConsoleUpdate(ConsoleUpdate str);
        Task OnLogFile(string str);
        Task OnState(State state);
        Task OnFreeSpace(DiskFreeSpace space);

        Task OnServiceSetting(ServiceSettingUpdate update);
        Task OnJlsCommandFiles(JLSCommandFiles files);
        Task OnAvsScriptFiles(AvsScriptFiles files);
        Task OnLogoData(LogoData logoData);
        Task OnDrcsData(DrcsImageUpdate update);
        Task OnAddResult(string requestId);

        Task OnOperationResult(string result);
        void Finish();
    }

    public enum RPCMethodId
    {
        SetSetting = 100,
        AddQueue,
        RemoveQueue,
        ChangeItem,
        PauseEncode,
        SetServiceSetting,
        AddDrcsMap,
        RequestSetting,
        RequestQueue,
        RequestLog,
        RequestConsole,
        RequestLogFile,
        RequestState,
        RequestFreeSpace,
        RequestServiceSetting,
        RequestLogoData,
        RequestDrcsImages,

        OnSetting = 200,
        OnQueueData,
        OnQueueUpdate,
        OnLogData,
        OnLogUpdate,
        OnConsole,
        OnConsoleUpdate,
        OnLogFile,
        OnState,
        OnFreeSpace,
        OnServiceSetting,
        OnLlsCommandFiles,
        OnAvsScriptFiles,
        OnLogoData,
        OnDrcsData,
        OnAddResult,
        OnOperationResult,
    }

    public struct RPCInfo
    {
        public RPCMethodId id;
        public object arg;
    }

    public static class RPCTypes
    {
        public static readonly Dictionary<RPCMethodId, Type> ArgumentTypes = new Dictionary<RPCMethodId, Type>() {
            { RPCMethodId.SetSetting, typeof(Setting) },
            { RPCMethodId.AddQueue, typeof(AddQueueDirectory) },
            { RPCMethodId.RemoveQueue, typeof(int) },
            { RPCMethodId.ChangeItem, typeof(ChangeItemData) },
            { RPCMethodId.PauseEncode, typeof(bool) },
            { RPCMethodId.SetServiceSetting, typeof(ServiceSettingUpdate) },
            { RPCMethodId.AddDrcsMap, typeof(DrcsImage) },
            { RPCMethodId.RequestSetting, null },
            { RPCMethodId.RequestQueue, null },
            { RPCMethodId.RequestLog, null },
            { RPCMethodId.RequestConsole, null },
            { RPCMethodId.RequestLogFile, typeof(LogItem) },
            { RPCMethodId.RequestState, null },
            { RPCMethodId.RequestFreeSpace, null },
            { RPCMethodId.RequestServiceSetting, null },
            { RPCMethodId.RequestLogoData, typeof(string) },
            { RPCMethodId.RequestDrcsImages, null },

            { RPCMethodId.OnSetting, typeof(Setting) },
            { RPCMethodId.OnQueueData, typeof(QueueData) },
            { RPCMethodId.OnQueueUpdate, typeof(QueueUpdate) },
            { RPCMethodId.OnLogData, typeof(LogData) },
            { RPCMethodId.OnLogUpdate, typeof(LogItem) },
            { RPCMethodId.OnConsole, typeof(ConsoleData) },
            { RPCMethodId.OnConsoleUpdate, typeof(ConsoleUpdate) },
            { RPCMethodId.OnLogFile, typeof(string) },
            { RPCMethodId.OnState, typeof(State) },
            { RPCMethodId.OnFreeSpace, typeof(DiskFreeSpace) },
            { RPCMethodId.OnServiceSetting, typeof(ServiceSettingUpdate) },
            { RPCMethodId.OnLlsCommandFiles, typeof(JLSCommandFiles) },
            { RPCMethodId.OnAvsScriptFiles, typeof(AvsScriptFiles) },
            { RPCMethodId.OnLogoData, typeof(LogoData) },
            { RPCMethodId.OnDrcsData, typeof(DrcsImageUpdate) },
            { RPCMethodId.OnAddResult, typeof(string) },
            { RPCMethodId.OnOperationResult, typeof(string) }
        };

        public static readonly int HEADER_SIZE = 6;

        private static byte[] Combine(params byte[][] arrays)
        {
            byte[] rv = new byte[arrays.Sum(a => a.Length)];
            int offset = 0;
            foreach (byte[] array in arrays)
            {
                System.Buffer.BlockCopy(array, 0, rv, offset, array.Length);
                offset += array.Length;
            }
            return rv;
        }

        private static byte[] CombineChunks(List<byte[]> arrays)
        {
            byte[] rv = new byte[arrays.Sum(a => a.Length) + arrays.Count * 4];
            int offset = 0;
            foreach (byte[] array in arrays)
            {
                System.Buffer.BlockCopy(
                    BitConverter.GetBytes((int)array.Length), 0, rv, offset, 4);
                offset += 4;
                System.Buffer.BlockCopy(array, 0, rv, offset, array.Length);
                offset += array.Length;
            }
            return rv;
        }

        private static List<MemoryStream> SplitChunks(byte[] bytes)
        {
            var ret = new List<MemoryStream>();
            for(int offset = 0; offset < bytes.Length; )
            {
                int sz = BitConverter.ToInt32(bytes, offset);
                offset += 4;
                ret.Add(new MemoryStream(bytes, offset, sz));
                offset += sz;
            }
            return ret;
        }

        private static List<BitmapFrame> GetImage(object obj)
        {
            if(obj is LogoData)
            {
                return new List<BitmapFrame> { ((LogoData)obj).Image };
            }
            if(obj is DrcsImage)
            {
                return new List<BitmapFrame> { ((DrcsImage)obj).Image };
            }
            if(obj is DrcsImageUpdate)
            {
                var update = (DrcsImageUpdate)obj;
                var ret = new List<BitmapFrame>();
                if(update.Image != null)
                {
                    ret.Add(update.Image.Image);
                }
                if (update.ImageList != null)
                {
                    foreach(var img in update.ImageList)
                    {
                        ret.Add(img.Image);
                    }
                }
                return ret;
            }
            return null;
        }

        private static Action<List<BitmapFrame>> ImageSetter(object obj)
        {
            if (obj is LogoData)
            {
                return image => { ((LogoData)obj).Image = image[0]; };
            }
            if (obj is DrcsImage)
            {
                return image => { ((DrcsImage)obj).Image = image[0]; };
            }
            if (obj is DrcsImageUpdate)
            {
                return images => {
                    var update = (DrcsImageUpdate)obj;
                    int idx = 0;
                    if (update.Image != null)
                    {
                        update.Image.Image = images[idx++];
                    }
                    if (update.ImageList != null)
                    {
                        foreach (var img in update.ImageList)
                        {
                            img.Image = images[idx++];
                        }
                    }
                };
            }
            return null;
        }

        public static byte[] Serialize(Type type, object obj)
        {
            var data = new List<byte[]>();
            {
                var ms = new MemoryStream();
                var serializer = new DataContractSerializer(type);
                serializer.WriteObject(ms, obj);
                data.Add(ms.ToArray());
            }
            // 画像だけ特別処理
            var image = GetImage(obj);
            if (image != null)
            {
                for(int i = 0; i < image.Count; ++i)
                {
                    if(image[i] != null)
                    {
                        var ms2 = new MemoryStream();
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(image[i]);
                        encoder.Save(ms2);
                        data.Add(ms2.ToArray());
                    }
                    else
                    {
                        data.Add(new byte[0]);
                    }
                }
            }
            return CombineChunks(data);
        }

        public static byte[] Serialize(RPCMethodId id, object obj)
        {
            Type type = ArgumentTypes[id];
            if (type == null)
            {
                return Combine(
                    BitConverter.GetBytes((short)id),
                    BitConverter.GetBytes((int)0));
            }
            var objbyes = Serialize(type, obj);
            //Debug.Print("Send: " + System.Text.Encoding.UTF8.GetString(objbyes));
            return Combine(
                BitConverter.GetBytes((short)id),
                BitConverter.GetBytes(objbyes.Length),
                objbyes);
        }

        public static object Deserialize(Type type, byte[] bytes)
        {
            var data = SplitChunks(bytes);
            var arg = new DataContractSerializer(type).ReadObject(data[0]);
            // 画像だけ特別処理
            var setter = ImageSetter(arg);
            if (setter != null)
            {
                List<BitmapFrame> images = new List<BitmapFrame>();
                for(int i = 1; i < data.Count; ++i)
                {
                    if(data[i].Length == 0)
                    {
                        images.Add(null);
                    }
                    else
                    {
                        images.Add(BitmapFrame.Create(data[i]));
                    }
                }
                setter(images);
            }
            return arg;
        }

        public static async Task<RPCInfo> Deserialize(Stream ns)
        {
            var headerbytes = await ReadBytes(ns, HEADER_SIZE);
            var id = (RPCMethodId)BitConverter.ToInt16(headerbytes, 0);
            var csize = BitConverter.ToInt32(headerbytes, 2);
            object arg = null;
            if (csize > 0)
            {
                var data = await RPCTypes.ReadBytes(ns, csize);
                //Debug.Print("Received: " + System.Text.Encoding.UTF8.GetString(data));
                arg = Deserialize(RPCTypes.ArgumentTypes[id], data);
            }
            return new RPCInfo() { id = id, arg = arg };
        }

        private static async Task<byte[]> ReadBytes(Stream ns, int size)
        {
            byte[] bytes = new byte[size];
            int readBytes = 0;
            while (readBytes < size)
            {
                readBytes += await ns.ReadAsync(
                    bytes, readBytes, size - readBytes);
            }
            return bytes;
        }

        public static Task RefreshRequest(this IEncodeServer server)
        {
            return Task.WhenAll(
                    server.RequestSetting(),
                    server.RequestQueue(),
                    server.RequestLog(),
                    server.RequestConsole(),
                    server.RequestState(),
                    server.RequestFreeSpace(),
                    server.RequestServiceSetting(),
                    server.RequestDrcsImages());
        }
    }

    // スタンドアロンでサーバ・クライアントをエミュレーションするためのアダプタ
    public class ClientAdapter : IUserClient
    {
        private IUserClient client;

        private static object Copy(Type type, object obj)
        {
            return RPCTypes.Deserialize(type, RPCTypes.Serialize(type, obj));
        }

        public ClientAdapter(IUserClient client)
        {
            this.client = client;
        }

        public void Finish()
        {
            client.Finish();
        }

        public Task OnConsole(ConsoleData str)
        {
            return client.OnConsole((ConsoleData)Copy(typeof(ConsoleData), str));
        }

        public Task OnConsoleUpdate(ConsoleUpdate str)
        {
            return client.OnConsoleUpdate((ConsoleUpdate)Copy(typeof(ConsoleUpdate), str));
        }

        public Task OnFreeSpace(DiskFreeSpace space)
        {
            return client.OnFreeSpace((DiskFreeSpace)Copy(typeof(DiskFreeSpace), space));
        }

        public Task OnJlsCommandFiles(JLSCommandFiles files)
        {
            return client.OnJlsCommandFiles((JLSCommandFiles)Copy(typeof(JLSCommandFiles), files));
        }

        public Task OnLogData(LogData data)
        {
            return client.OnLogData((LogData)Copy(typeof(LogData), data));
        }

        public Task OnLogFile(string str)
        {
            return client.OnLogFile((string)Copy(typeof(string), str));
        }

        public Task OnLogoData(LogoData logoData)
        {
            return client.OnLogoData((LogoData)Copy(typeof(LogoData), logoData));
        }

        public Task OnLogUpdate(LogItem newLog)
        {
            return client.OnLogUpdate((LogItem)Copy(typeof(LogItem), newLog));
        }

        public Task OnOperationResult(string result)
        {
            return client.OnOperationResult((string)Copy(typeof(string), result));
        }

        public Task OnQueueData(QueueData data)
        {
            return client.OnQueueData((QueueData)Copy(typeof(QueueData), data));
        }

        public Task OnQueueUpdate(QueueUpdate update)
        {
            return client.OnQueueUpdate((QueueUpdate)Copy(typeof(QueueUpdate), update));
        }

        public Task OnServiceSetting(ServiceSettingUpdate service)
        {
            return client.OnServiceSetting((ServiceSettingUpdate)Copy(typeof(ServiceSettingUpdate), service));
        }

        public Task OnSetting(Setting setting)
        {
            return client.OnSetting((Setting)Copy(typeof(Setting), setting));
        }

        public Task OnState(State state)
        {
            return client.OnState((State)Copy(typeof(State), state));
        }

        public Task OnAvsScriptFiles(AvsScriptFiles files)
        {
            return client.OnAvsScriptFiles((AvsScriptFiles)Copy(typeof(AvsScriptFiles), files));
        }

        public Task OnDrcsData(DrcsImageUpdate update)
        {
            return client.OnDrcsData((DrcsImageUpdate)Copy(typeof(DrcsImageUpdate), update));
        }

        public Task OnAddResult(string requestId)
        {
            return client.OnAddResult((string)Copy(typeof(string), requestId));
        }
    }
}