namespace HomeHub;

/// <summary>The hub's log lines, source-generated so a disabled level costs nothing.</summary>
internal static partial class HubLog
{
    [LoggerMessage(1, LogLevel.Information, "Hub pronto com {Count} dispositivos. Token do cliente em {Path}.")]
    public static partial void Ready(ILogger logger, int count, string path);

    [LoggerMessage(10, LogLevel.Information, "OpenBao iniciado (pid {Pid}).")]
    public static partial void VaultStarted(ILogger logger, int pid);

    [LoggerMessage(11, LogLevel.Information, "Cofre destrancado.")]
    public static partial void VaultUnsealed(ILogger logger);

    [LoggerMessage(12, LogLevel.Information, "Cofre pronto em {Address}.")]
    public static partial void VaultReady(ILogger logger, Uri address);

    [LoggerMessage(13, LogLevel.Information, "Cofre inicializado: montagem {Mount}, política {Policy}.")]
    public static partial void VaultInitialized(ILogger logger, string mount, string policy);

    [LoggerMessage(14, LogLevel.Information, "Token da casa emitido no cofre.")]
    public static partial void TokenIssued(ILogger logger);

    [LoggerMessage(15, LogLevel.Warning, "Renovação do token do cofre falhou (HTTP {Status}); emitindo outro.")]
    public static partial void RenewRefused(ILogger logger, int status);

    [LoggerMessage(16, LogLevel.Warning, "Não consegui renovar o token do cofre.")]
    public static partial void RenewFailed(ILogger logger, Exception exception);
}
