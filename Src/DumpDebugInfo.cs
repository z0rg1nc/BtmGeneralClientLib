using System;
using System.IO;
using System.Linq;
using System.Text;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using NLog;

namespace BtmI2p.BitMoneyClient.Lib
{
    public static class DumpDebugInfo
    {
        private static Logger _log = LogManager.GetCurrentClassLogger();

        private static string GetDumpString()
        {
            var resultSb = new StringBuilder();
            var sems = SemaphoreSlimDisposableWrapper.WrappersDb;
            var shs = FuncWrapperDisposable.FuncWrapperDb;
            resultSb.AppendFormat("SemaphoreSlim: {0}{1}{0}", Environment.NewLine, sems.Select(x =>
            {
                long ms1;
                if (x.EnterTime != DateTime.MinValue)
                {
                    ms1 = (long)((DateTime.UtcNow - x.EnterTime).TotalMilliseconds);
                    return new
                    {
                        A = ms1,
                        B = string.Format("Occupied for {1}ms {0}", x.MthdName, ms1)
                    };
                }
                if (x.TryEnterTime != DateTime.MinValue)
                {
                    ms1 = (long)((DateTime.UtcNow - x.TryEnterTime).TotalMilliseconds);
                    return new
                    {
                        A = ms1,
                        B = string.Format("Trying to enter for {1}ms {0}", x.MthdName, ms1)
                    };
                }
                return new
                {
                    A = 0l,
                    B = string.Format("{0} unexpected data", x.MthdName)
                };
            }).OrderByDescending(x => x.A).Select(x => x.B).WriteObjectToJson());
            resultSb.AppendFormat("FuncWrapper: {0}{1}{0}", Environment.NewLine, shs.Select(x =>
            {
                long ms1 = 0;
                if (x.EnterTime != DateTime.MinValue)
                {
                    ms1 = (long)((DateTime.UtcNow - x.EnterTime).TotalMilliseconds);
                    return new
                    {
                        A = ms1,
                        B = string.Format("Working for {1}ms {0}", x.FuncWrapperId.ToString(), ms1)
                    };
                }
                return new
                {
                    A = 0l,
                    B = string.Format("{0} unexpected data", x.FuncWrapperId.ToString())
                };
            }).OrderByDescending(x => x.A).Select(x => x.B).WriteObjectToJson());
            return resultSb.ToString();
        }

        public static void DumpToFile(string filePath)
        {
            File.WriteAllText(filePath, GetDumpString());
        }

        public static void DumpToLog()
        {
            _log.Trace(GetDumpString());
        }
    }
}
