using System;
using System.Collections.Generic;
using Eplan.EplApi.RemoteClient;
using Eplan.EplApi.Remoting;

namespace EplanRemote
{
    /// <summary>
    /// Sends an EPLAN action to an already-running EPLAN process via EplanCommunicationServer.
    /// Usage: EplanRemote.exe [actionName] [paramName] [paramValue]
    /// Default action is ExecuteScript with ScriptFile = C:\Temp\EplanScripts\LoadAddIn.cs
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string action = args.Length > 0 ? args[0] : "ExecuteScript";
            string pName  = args.Length > 1 ? args[1] : "ScriptFile";
            string pValue = args.Length > 2 ? args[2] : @"C:\Temp\EplanScripts\LoadAddIn.cs";

            EplanRemoteClient client = null;
            try
            {
                client = new EplanRemoteClient();

                List<EplanServerData> servers;
                client.GetActiveEplanServersOnLocalMachine(out servers);

                int count = servers == null ? 0 : servers.Count;
                Console.WriteLine("[1] active EPLAN servers found: " + count);
                if (count == 0)
                {
                    Console.WriteLine("    nothing to talk to. Aborting.");
                    return 2;
                }

                foreach (EplanServerData s in servers)
                {
                    Console.WriteLine("    pid=" + s.EplanProcessID
                        + " name=" + s.ServerName
                        + " port=" + s.ServerPort
                        + " ver=" + s.EplanVersion
                        + " err=" + s.ErrorMsg);
                }

                EplanServerData target = servers[0];
                // NOTE: first parameter is the COMPUTER (host) name, not ServerName.
                string host = args.Length > 3 ? args[3] : "localhost";
                Console.WriteLine("[2] connecting to " + host + ":" + target.ServerPort + " (server=" + target.ServerName + ") ...");
                client.Connect(host, target.ServerPort.ToString());
                Console.WriteLine("    connected.");

                try
                {
                    Console.WriteLine("[3] ping -> " + client.Ping());
                }
                catch (Exception exPing)
                {
                    Console.WriteLine("[3] ping failed: " + exPing.Message);
                }

                CallingContext ctx = new CallingContext();
                bool hasParam = args.Length > 1;
                if (hasParam)
                {
                    ctx.Set(pName, pValue);
                    Console.WriteLine("[4] ExecuteAction '" + action + "' with " + pName + "=" + pValue);
                    client.ExecuteAction(action, ref ctx);
                }
                else
                {
                    Console.WriteLine("[4] ExecuteAction '" + action + "' (no parameters)");
                    client.ExecuteAction(action);
                }
                Console.WriteLine("    done. Succeed=" + ctx.Succeed + " Message=" + ctx.Message);

                object result;
                if (ctx.Get("Result", out result) && result != null)
                {
                    Console.WriteLine("    Result=" + result);
                }

                client.Disconnect();
                Console.WriteLine("[5] disconnected.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex.GetType().FullName);
                Console.WriteLine("  message: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("  inner  : " + ex.InnerException.Message);
                }
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
            finally
            {
                if (client != null)
                {
                    try { client.Dispose(); } catch { }
                }
            }
        }
    }
}
