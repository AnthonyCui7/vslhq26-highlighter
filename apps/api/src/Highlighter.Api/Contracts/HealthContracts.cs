namespace Highlighter.Api.Contracts;

public record HealthDto(
    string Status,
    HealthEnvDto Env,
    HealthWorkerDto Worker,
    HealthBinariesDto Binaries,
    HealthSupabaseDto Supabase,
    HealthOutputsDto Outputs,
    HealthCleanupDto? Cleanup,
    int? OrphanCandidates);

/// <summary>Presence booleans only — never values.</summary>
public record HealthEnvDto(
    bool SupabaseUrl,
    bool SupabaseServiceRoleKey,
    bool DeepgramApiKey,
    bool AzureSpeech,
    bool AzureOpenAI,
    bool OpenRouterApiKey,
    bool UploadPostApiKey,
    bool UploadPostUser);

public record HealthWorkerDto(bool Resolved, string? Command);

public record HealthBinariesDto(bool Ffmpeg, bool YtDlp, bool Streamlink);

public record HealthSupabaseDto(bool Configured, bool? Reachable);

public record HealthOutputsDto(string Root, bool Writable, string JobLogRoot);

public record HealthCleanupDto(int Pending, int Failed);
