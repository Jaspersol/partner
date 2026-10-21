using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Partner.Api.Settings;
using Partner.Core.Agent.Skills;

namespace Partner.Api.Endpoints;

/// <summary>
/// Adding and removing workspace skills: a skill is <c>&lt;workspace&gt;/skills/&lt;id&gt;/SKILL.md</c> (plus
/// whatever files sit beside it), so creating one writes that file, uploading one unpacks it, and deleting one
/// removes its directory.
/// <para>
/// Nothing is written until <see cref="SkillLoader"/> has accepted it from a staging directory, so an upload
/// that would only show up as a warning is refused up front instead. The live registry is reloaded afterwards, so
/// the next turn sees the change without a restart.
/// </para>
/// </summary>
public static partial class SkillEndpoints
{
    /// <summary>An upload is a text file or a small archive; this is a ceiling, not an expectation.</summary>
    public const long MaxUploadBytes = 5L * 1024 * 1024;

    /// <summary>What an archive may unpack to, counted as bytes actually written (the declared size can lie).</summary>
    public const long MaxUnpackedBytes = 20L * 1024 * 1024;

    private const int MaxEntries = 200;
    private const int MaxIdLength = 64;
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 200;
    private const string SkillsDirectory = "skills";
    private const string SkillFile = "SKILL.md";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex IdPattern();

    public sealed record CreateSkillRequest(string? SkillId, string? Name, string? Description, string? Instructions);

    public static void Map(WebApplication app, ApiContext ctx, string workspace)
    {
        app.MapPost("/api/skills", (CreateSkillRequest request, ConsumerHost consumer) =>
        {
            var skillId = (request.SkillId ?? string.Empty).Trim();
            var name = (request.Name ?? string.Empty).Trim();
            var description = (request.Description ?? string.Empty).Trim();
            var instructions = request.Instructions ?? string.Empty;

            if (!IsValidId(skillId))
            {
                return Results.BadRequest(new { error = $"技能 ID 只能用小写字母、数字、点、下划线和连字符，以字母或数字开头，最长 {MaxIdLength} 个字符。" });
            }

            if (name.Length == 0 || name.Length > MaxNameLength || HasLineBreak(name))
            {
                return Results.BadRequest(new { error = $"名称必填，单行，最长 {MaxNameLength} 个字符。" });
            }

            if (description.Length == 0 || description.Length > MaxDescriptionLength || HasLineBreak(description))
            {
                return Results.BadRequest(new { error = $"描述必填，单行，最长 {MaxDescriptionLength} 个字符。" });
            }

            if (string.IsNullOrWhiteSpace(instructions))
            {
                return Results.BadRequest(new { error = "说明不能为空。" });
            }

            var target = SkillPath(workspace, skillId);
            if (Directory.Exists(target))
            {
                return Results.Conflict(new { error = $"已经有一个叫 '{skillId}' 的技能。" });
            }

            Directory.CreateDirectory(target);
            try
            {
                File.WriteAllText(
                    Path.Combine(target, SkillFile),
                    $"---\nname: {name}\ndescription: {description}\n---\n{instructions.TrimEnd()}\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // A directory with no SKILL.md would show up as a warning on every later scan.
                Directory.Delete(target, recursive: true);
                throw;
            }

            consumer.ReloadSkills();
            return Results.Ok(new { skillId, name });
        });

        // Raw body plus the name in a header, like the attachment upload. A `.md` is one SKILL.md; a `.zip` is a
        // skill folder (SKILL.md at its root, or inside the one folder it holds).
        app.MapPost("/api/skills/upload", async (HttpRequest request, ConsumerHost consumer, CancellationToken ct) =>
        {
            var encoded = request.Headers["X-File-Name"].ToString();
            var fileName = Path.GetFileName((encoded.Contains('%') ? Uri.UnescapeDataString(encoded) : encoded).Replace('\\', '/'));
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension is not (".md" or ".zip"))
            {
                return Results.BadRequest(new { error = "技能需要是 .md（SKILL.md）或 .zip（技能文件夹）文件。" });
            }

            if (request.ContentLength is > MaxUploadBytes)
            {
                return Results.BadRequest(new { error = $"文件太大，上限 {MaxUploadBytes / 1024 / 1024} MB。" });
            }

            var bytes = await ReadBodyAsync(request.Body, MaxUploadBytes, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                return Results.BadRequest(new { error = $"文件太大，上限 {MaxUploadBytes / 1024 / 1024} MB。" });
            }

            if (bytes.Length == 0)
            {
                return Results.BadRequest(new { error = "文件是空的。" });
            }

            var staging = Path.Combine(workspace, ".skill-staging", Guid.NewGuid().ToString("N"));
            try
            {
                var unpacked = Path.Combine(staging, "unpacked");
                Directory.CreateDirectory(unpacked);

                string? failure;
                string? preferredId;
                if (extension == ".md")
                {
                    File.WriteAllBytes(Path.Combine(unpacked, SkillFile), bytes);
                    failure = null;
                    preferredId = Path.GetFileNameWithoutExtension(fileName);
                }
                else
                {
                    failure = Unpack(bytes, unpacked);
                    preferredId = Path.GetFileNameWithoutExtension(fileName);

                    // A folder zipped with its parent: step into the one directory that holds SKILL.md.
                    if (failure is null && !File.Exists(Path.Combine(unpacked, SkillFile)))
                    {
                        var children = Directory.GetDirectories(unpacked);
                        var files = Directory.GetFiles(unpacked);
                        if (children.Length == 1 && files.Length == 0 && File.Exists(Path.Combine(children[0], SkillFile)))
                        {
                            preferredId = Path.GetFileName(children[0]);
                            unpacked = children[0];
                        }
                        else
                        {
                            failure = "压缩包里找不到 SKILL.md（应在根目录，或在唯一的一个文件夹里）。";
                        }
                    }
                }

                if (failure is not null)
                {
                    return Results.BadRequest(new { error = failure });
                }

                // The loader is the authority on what a valid skill is, so the candidate goes through it under a
                // throwaway id first; the id it will live under is decided after, from the name if need be.
                var probe = Path.Combine(staging, "probe");
                var probeSkill = Path.Combine(probe, SkillsDirectory, "candidate");
                Directory.CreateDirectory(Path.GetDirectoryName(probeSkill)!);
                Directory.Move(unpacked, probeSkill);

                var loaded = new SkillLoader().Load(probe);
                if (loaded.Skills.Count != 1)
                {
                    var reason = loaded.Warnings.Count > 0
                        ? string.Join("；", loaded.Warnings.Select(static warning => StripPath(warning)))
                        : "不是有效的技能。";
                    return Results.BadRequest(new { error = $"这不是有效的技能：{reason}" });
                }

                var skill = loaded.Skills[0];
                var skillId = FirstValidId(preferredId, skill.Name) ?? $"skill-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var target = SkillPath(workspace, skillId);
                if (Directory.Exists(target))
                {
                    return Results.Conflict(new { error = $"已经有一个叫 '{skillId}' 的技能。要替换它，请先删除旧的。" });
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(probeSkill, target);

                consumer.ReloadSkills();
                return Results.Ok(new { skillId, name = skill.Name });
            }
            finally
            {
                TryDelete(staging);
            }
        });

        app.MapDelete("/api/skills/{skillId}", (string skillId, ConsumerHost consumer) =>
        {
            if (!IsValidId(skillId))
            {
                return Results.NotFound(new { error = $"没有叫 '{skillId}' 的技能。" });
            }

            var target = SkillPath(workspace, skillId);
            if (!Directory.Exists(target))
            {
                return Results.NotFound(new { error = $"没有叫 '{skillId}' 的技能。" });
            }

            Directory.Delete(target, recursive: true);

            // A deny-list entry for a skill that is gone would silently switch a later skill of the same name off.
            var settings = ctx.Settings.Load();
            if (settings.Skills.Disabled.Contains(skillId, StringComparer.Ordinal))
            {
                ctx.Settings.Save(settings with
                {
                    Skills = new SkillSettings
                    {
                        Disabled = settings.Skills.Disabled.Where(id => !string.Equals(id, skillId, StringComparison.Ordinal)).ToList(),
                    },
                });
            }

            consumer.ReloadSkills();
            return Results.Ok(new { deleted = skillId });
        });
    }

    private static bool IsValidId(string skillId) =>
        skillId.Length is > 0 and <= MaxIdLength && IdPattern().IsMatch(skillId);

    private static bool HasLineBreak(string text) => text.Contains('\n') || text.Contains('\r');

    /// <summary>The skill's directory, with the id already validated so it cannot name anything outside <c>skills</c>.</summary>
    private static string SkillPath(string workspace, string skillId) =>
        Path.GetFullPath(Path.Combine(workspace, SkillsDirectory, skillId));

    /// <summary>The first candidate that is, or can be made into, a valid id: lower-cased, anything else folded to <c>-</c>.</summary>
    private static string? FirstValidId(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var slug = Regex.Replace((candidate ?? string.Empty).ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.', '_');
            if (slug.Length > MaxIdLength)
            {
                slug = slug[..MaxIdLength].TrimEnd('-', '.', '_');
            }

            // "SKILL.md" uploaded on its own says nothing about what the skill is called.
            if (slug is not ("" or "skill") && IsValidId(slug))
            {
                return slug;
            }
        }

        return null;
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream body, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Unpacks into <paramref name="destination"/>; returns why it refused, or null on success.</summary>
    private static string? Unpack(byte[] archive, string destination)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return "这不是有效的 zip 文件。";
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
            {
                return $"压缩包里的文件太多（上限 {MaxEntries} 个）。";
            }

            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || name.EndsWith('/'))
                {
                    continue;
                }

                // A name is a label, never a location: nothing rooted, nothing that climbs out.
                if (name.StartsWith('/') || name.Split('/').Any(static part => part == ".." || part.Contains(':')))
                {
                    return "压缩包里有不安全的路径。";
                }

                var path = Path.GetFullPath(Path.Combine(destination, name));
                if (!path.StartsWith(root, StringComparison.Ordinal))
                {
                    return "压缩包里有不安全的路径。";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = entry.Open();
                using var target = File.Create(path);
                var chunk = new byte[81920];
                int read;
                while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > MaxUnpackedBytes)
                    {
                        return $"解压后太大（上限 {MaxUnpackedBytes / 1024 / 1024} MB）。";
                    }

                    target.Write(chunk, 0, read);
                }
            }
        }

        return null;
    }

    /// <summary>The loader's warnings name the staging path, which means nothing to the person reading them.</summary>
    private static string StripPath(string warning)
    {
        var colon = warning.IndexOf('：');
        return colon >= 0 ? warning[(colon + 1)..] : warning;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
            // Staging leftovers are harmless; they are not scanned.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
