using System.Threading.Tasks;
using JetBrains.Application.BuildScript;
using JetBrains.Application.BuildScript.PreCompile;
using JetBrains.Build;
using JetBrains.Lifetimes;
using JetBrains.Util;

namespace DefaultNamespace
{
  public static class GradlewPrepare
  {
    [BuildStep]
    public static async Task<LocalPrepareWorkingCopy> CallGradlewRdgenPwcTask(ProductHomeDirArtifact homeDir, Lifetime lifetime, ILogger logger)
    {
      var workingDir = homeDir.ProductHomeDir / "Plugins" / "fsharp-support" / "rider-fsharp";
      var path = PlatformUtil.RuntimePlatform == PlatformUtil.Platform.Windows 
        ? workingDir / "gradlew.bat"
        : workingDir / "gradlew";

      var logPrefix = "FSHARP_RDGEN: ";
        
      var arguments = new CommandLineBuilderJet();
      arguments.AppendParameterWithQuoting("rdgenPwc");
      
      var startInfo = new InvokeChildProcess.StartInfo(path)
      {
        CurrentDirectory = workingDir, 
        Arguments = arguments,
        Pipe = InvokeChildProcess.PipeStreams.Custom((chunk, isError, log) =>
        {
          if (isError)
            log.Error(logPrefix+ chunk);
          else
            log.Verbose(logPrefix + chunk);
        }),
        StartInJob = PlatformUtil.RuntimePlatform == PlatformUtil.Platform.Windows
      };

      logger.Info("Start fsharp rdgen call");
      await InvokeChildProcess.InvokeCore(lifetime, startInfo, InvokeChildProcess.SyncAsync.Async, logger);
      logger.Info("End fsharp rdgen call");
      
      return LocalPrepareWorkingCopy.Item;
    }
  }
}