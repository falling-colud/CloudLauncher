using System.Text.Json;

namespace CloudLauncher.Shared;

// A pack's planning boards travel apart from its files: every edit is pushed as it happens and the
// other launchers watching the pack pick it up, instead of waiting for the next pack upload. The
// document itself is the launcher's plans.json, kept verbatim; the server only numbers its versions.

/// <summary>The pack's planning boards as the server last stored them.</summary>
/// <param name="Revision">Goes up by one on every stored change. 0 = the pack has no boards on the
/// server yet (and <paramref name="Plans"/> is null).</param>
/// <param name="Plans">The plans.json document, exactly as the launcher that stored it wrote it.</param>
public sealed record PackPlansDocument(long Revision, DateTimeOffset? UpdatedAt, JsonElement? Plans);

/// <summary>Stores a new version of the pack's boards.</summary>
/// <param name="BaseRevision">The revision the change was made on. The server refuses with 409 and
/// the current <see cref="PackPlansDocument"/> when someone stored a newer one in the meantime, so
/// the launcher can merge and try again rather than overwrite it.</param>
public sealed record PutPackPlansRequest(long BaseRevision, JsonElement Plans);

/// <summary>The revision a successful store produced.</summary>
public sealed record PackPlansStored(long Revision, DateTimeOffset UpdatedAt);
