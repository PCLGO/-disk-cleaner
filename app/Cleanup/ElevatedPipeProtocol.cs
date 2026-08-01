using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DiskCleanupAssistant.Models;
using DiskCleanupAssistant.Rules;

namespace DiskCleanupAssistant.Cleanup
{
    public static class ElevatedPipeProtocol
    {
        public static async Task<List<ActionResult>> ExecuteElevatedAsync(List<CandidateRecord> items, CancellationToken token)
        {
            var pipeName = "DiskCleanupAssistant-" + Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security))
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var start = new ProcessStartInfo(exe, "--elevated-executor " + pipeName + " " + nonce)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                Process.Start(start);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                }
                var plan = new CleanupPlan { Nonce = nonce, CreatedUtc = DateTime.UtcNow, Items = items };
                WriteJson(server, plan, typeof(CleanupPlan));
                await server.FlushAsync(token).ConfigureAwait(false);
                var envelope = (ActionResultEnvelope)ReadJson(server, typeof(ActionResultEnvelope));
                if (envelope == null || envelope.Nonce != nonce) throw new InvalidDataException("管理员执行器返回的随机令牌不匹配");
                return envelope.Results ?? new List<ActionResult>();
            }
        }

        public static async Task<int> RunElevatedClientAsync(string pipeName, string nonce)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await client.ConnectAsync(15000).ConfigureAwait(false);
                    var plan = (CleanupPlan)ReadJson(client, typeof(CleanupPlan));
                    if (plan == null || plan.Nonce != nonce || (DateTime.UtcNow - plan.CreatedUtc).TotalMinutes > 2)
                        throw new InvalidDataException("清理计划无效或已过期");
                    var executor = new CleanupExecutor(new RuleEngine());
                    var results = await executor.ExecuteAsync(plan.Items ?? new List<CandidateRecord>(), CancellationToken.None).ConfigureAwait(false);
                    WriteJson(client, new ActionResultEnvelope { Nonce = nonce, Results = results }, typeof(ActionResultEnvelope));
                    await client.FlushAsync().ConfigureAwait(false);
                }
                return 0;
            }
            catch { return 2; }
        }

        private static void WriteJson(Stream stream, object value, Type type)
        {
            var serializer = new DataContractJsonSerializer(type);
            using (var buffer = new MemoryStream())
            {
                serializer.WriteObject(buffer, value);
                var bytes = buffer.ToArray();
                var length = BitConverter.GetBytes(bytes.Length);
                stream.Write(length, 0, length.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static object ReadJson(Stream stream, Type type)
        {
            var lengthBytes = ReadExact(stream, 4);
            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("管道消息长度无效");
            var payload = ReadExact(stream, length);
            using (var buffer = new MemoryStream(payload))
                return new DataContractJsonSerializer(type).ReadObject(buffer);
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }
    }
}
