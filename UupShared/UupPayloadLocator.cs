using System.IO;

namespace OneCore_Image_Designer.Services
{
    public static class UupPayloadLocator
    {
        public static string ResolvePayloadPath(string snapshotPath, string? payloadFile)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
                throw new ArgumentException("A snapshot path is required.", nameof(snapshotPath));

            var fileName = string.IsNullOrWhiteSpace(payloadFile)
                ? Path.GetFileNameWithoutExtension(snapshotPath) + ".data.json.gz"
                : payloadFile;
            if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
                throw new InvalidDataException("The snapshot payload must be a file in the snapshot directory.");
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!, fileName));
        }

        public static T FindUpdate<T>(IReadOnlyDictionary<string, T> payloads, string updateId)
        {
            if (string.IsNullOrWhiteSpace(updateId))
                throw new ArgumentException("An update ID is required.", nameof(updateId));
            foreach (var payload in payloads)
                if (string.Equals(payload.Key, updateId, StringComparison.OrdinalIgnoreCase))
                    return payload.Value;
            throw new KeyNotFoundException($"Update {updateId} is not present in the scan payload.");
        }
    }
}
