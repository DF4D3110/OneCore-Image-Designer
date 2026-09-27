
namespace Img2Ffu.Writer.Manifest
{
    internal class FullFlashManifest
    {
        public required string OSVersion;
        public string AntiTheftVersion = "1.1"; // Allow flashing on all devices
        public string Description = "Update on: " + DateTime.Now.ToString("u") + "::\r\n";

        public string StateSeparationLevel = "0";
        public string UEFI = "True";
        public string Version = "2.0";
        public required string DevicePlatformId3;
        public required string DevicePlatformId2;
        public required string DevicePlatformId1;
        public required string DevicePlatformId0;
    }
}