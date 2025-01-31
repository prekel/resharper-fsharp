using JetBrains.Util;
using JetBrains.Util.Logging;

namespace JetBrains.ReSharper.Plugins.FSharp.Fantomas.Protocol
{
  public static class FantomasProtocolConstants
  {
    public const string PROCESS_FILENAME = "JetBrains.ReSharper.Plugins.FSharp.Fantomas.Host.exe";
    public const string PARENT_PROCESS_PID_ENV_VARIABLE = "FSHARP_FANTOMAS_PROCESS_PID";
    public static readonly FileSystemPath LogFolder = Logger.LogFolderPath.Combine("Fantomas");
  }
}
