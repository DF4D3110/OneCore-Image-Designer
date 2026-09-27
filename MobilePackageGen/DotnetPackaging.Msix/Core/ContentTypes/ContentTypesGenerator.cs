using Path = System.IO.Path;

namespace DotnetPackaging.Msix.Core.ContentTypes;

/// <summary>
/// Genera el modelo de tipos de contenido ([Content_Types].xml) emulando el comportamiento de makeappx.
/// </summary>
public static class ContentTypesGenerator
{
    // Diccionario de mappings por defecto, ajustado para emular makeappx:
    private static readonly Dictionary<string, string> DefaultMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        { "aiff", "audio/x-aiff" },
        { "appx", "application/vnd.ms-appx" },
        { "atom", "application/atom+xml" },
        { "au", "audio/basic" },
        { "avi", "video/avi" },
        { "b64", "application/base64" },
        { "bmp", "image/bmp" },
        { "c", "text/plain" },
        { "cab", "application/vnd.ms-cab-compressed" },
        { "cpp", "text/plain" },
        { "cs", "text/plain" },
        { "css", "text/css" },
        { "csv", "text/csv" },
        { "dll", "application/x-msdownload" },
        { "doc", "application/msword" },
        { "docm", "application/vnd.ms-word.document.macroenabled.12" },
        { "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { "dot", "application/msword" },
        { "dotm", "application/vnd.ms-word.document.macroenabled.12" },
        { "dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { "dtd", "application/xml-dtd" },
        { "emf", "image/x-emf" },
        { "exe", "application/x-msdownload" },
        { "gif", "image/gif" },
        { "gz", "application/x-gzip-compressed" },
        { "h", "text/plain" },
        { "htm", "text/html" },
        { "html", "text/html" },
        { "ico", "image/vnd.microsoft.icon" },
        { "java", "application/java" },
        { "jpeg", "image/jpeg" },
        { "jpg", "image/jpeg" },
        { "js", "application/x-javascript" },
        { "json", "application/json" },
        { "m4a", "audio/mp4" },
        { "mid", "audio/mid" },
        { "mov", "video/quicktime" },
        { "mp3", "audio/mpeg" },
        { "mpeg", "video/mpeg" },
        { "mpg", "video/mpeg" },
        { "p7s", "application/x-pkcs7-signature" },
        { "pdf", "application/pdf" },
        { "png", "image/png" },
        { "pot", "application/vnd.ms-powerpoint" },
        { "potm", "application/vnd.ms-powerpoint.template.macroenabled.12" },
        { "potx", "application/vnd.openxmlformats-officedocument.presentationml.template" },
        { "ppa", "application/vnd.ms-powerpoint" },
        { "ppam", "application/vnd.ms-powerpoint.addin.macroenabled.12" },
        { "pps", "application/vnd.ms-powerpoint" },
        { "ppsm", "application/vnd.ms-powerpoint.slideshow.macroenabled.12" },
        { "ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow" },
        { "ppt", "application/vnd.ms-powerpoint" },
        { "pptm", "application/vnd.ms-powerpoint.presentation.macroenabled.12" },
        { "pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { "ps", "application/postscript" },
        { "rar", "application/x-rar-compressed" },
        { "rss", "application/rss+xml" },
        { "rtf", "text/richtext" },
        { "sct", "text/scriptlet" },
        { "smf", "audio/mid" },
        { "soap", "application/soap+xml" },
        { "svg", "image/svg+xml" },
        { "tar", "application/x-tar" },
        { "tif", "image/tiff" },
        { "tiff", "image/tiff" },
        { "txt", "text/plain" },
        { "wav", "audio/wav" },
        { "wma", "audio/x-ms-wma" },
        { "wmf", "image/x-wmf" },
        { "wmv", "video/x-ms-wmv" },
        { "xaml", "application/xaml+xml" },
        { "xap", "application/x-silverlight-app" },
        { "xbap", "application/x-ms-xbap" },
        { "xhtml", "application/xhtml+xml" },
        { "xla", "application/vnd.ms-excel" },
        { "xlam", "application/vnd.ms-excel.addin.macroenabled.12" },
        { "xls", "application/vnd.ms-excel" },
        { "xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12" },
        { "xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12" },
        { "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { "xlt", "application/vnd.ms-excel" },
        { "xltm", "application/vnd.ms-excel.template.macroEnabled.12" },
        { "xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template" },
        //{ "xml", "text/xml" },
        { "xml", "application/vnd.ms-appx.manifest+xml" },
        { "xsd", "text/xml" },
        { "xsl", "application/xslt+xml" },
        { "xslt", "application/xslt+xml" },
        { "zip", "application/x-zip-compressed" },
        // Puedes agregar más según sea necesario.
    };


    // Diccionario de mappings por defecto, ajustado para emular makeappx:
    private static readonly Dictionary<string, string> DefaultMappingsForBundles = new(StringComparer.OrdinalIgnoreCase)
    {
        { "aiff", "audio/x-aiff" },
        { "appx", "application/vnd.ms-appx" },
        { "atom", "application/atom+xml" },
        { "au", "audio/basic" },
        { "avi", "video/avi" },
        { "b64", "application/base64" },
        { "bmp", "image/bmp" },
        { "c", "text/plain" },
        { "cab", "application/vnd.ms-cab-compressed" },
        { "cpp", "text/plain" },
        { "cs", "text/plain" },
        { "css", "text/css" },
        { "csv", "text/csv" },
        { "dll", "application/x-msdownload" },
        { "doc", "application/msword" },
        { "docm", "application/vnd.ms-word.document.macroenabled.12" },
        { "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { "dot", "application/msword" },
        { "dotm", "application/vnd.ms-word.document.macroenabled.12" },
        { "dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { "dtd", "application/xml-dtd" },
        { "emf", "image/x-emf" },
        { "exe", "application/x-msdownload" },
        { "gif", "image/gif" },
        { "gz", "application/x-gzip-compressed" },
        { "h", "text/plain" },
        { "htm", "text/html" },
        { "html", "text/html" },
        { "ico", "image/vnd.microsoft.icon" },
        { "java", "application/java" },
        { "jpeg", "image/jpeg" },
        { "jpg", "image/jpeg" },
        { "js", "application/x-javascript" },
        { "json", "application/json" },
        { "m4a", "audio/mp4" },
        { "mid", "audio/mid" },
        { "mov", "video/quicktime" },
        { "mp3", "audio/mpeg" },
        { "mpeg", "video/mpeg" },
        { "mpg", "video/mpeg" },
        { "p7s", "application/x-pkcs7-signature" },
        { "pdf", "application/pdf" },
        { "png", "image/png" },
        { "pot", "application/vnd.ms-powerpoint" },
        { "potm", "application/vnd.ms-powerpoint.template.macroenabled.12" },
        { "potx", "application/vnd.openxmlformats-officedocument.presentationml.template" },
        { "ppa", "application/vnd.ms-powerpoint" },
        { "ppam", "application/vnd.ms-powerpoint.addin.macroenabled.12" },
        { "pps", "application/vnd.ms-powerpoint" },
        { "ppsm", "application/vnd.ms-powerpoint.slideshow.macroenabled.12" },
        { "ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow" },
        { "ppt", "application/vnd.ms-powerpoint" },
        { "pptm", "application/vnd.ms-powerpoint.presentation.macroenabled.12" },
        { "pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { "ps", "application/postscript" },
        { "rar", "application/x-rar-compressed" },
        { "rss", "application/rss+xml" },
        { "rtf", "text/richtext" },
        { "sct", "text/scriptlet" },
        { "smf", "audio/mid" },
        { "soap", "application/soap+xml" },
        { "svg", "image/svg+xml" },
        { "tar", "application/x-tar" },
        { "tif", "image/tiff" },
        { "tiff", "image/tiff" },
        { "txt", "text/plain" },
        { "wav", "audio/wav" },
        { "wma", "audio/x-ms-wma" },
        { "wmf", "image/x-wmf" },
        { "wmv", "video/x-ms-wmv" },
        { "xaml", "application/xaml+xml" },
        { "xap", "application/x-silverlight-app" },
        { "xbap", "application/x-ms-xbap" },
        { "xhtml", "application/xhtml+xml" },
        { "xla", "application/vnd.ms-excel" },
        { "xlam", "application/vnd.ms-excel.addin.macroenabled.12" },
        { "xls", "application/vnd.ms-excel" },
        { "xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12" },
        { "xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12" },
        { "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { "xlt", "application/vnd.ms-excel" },
        { "xltm", "application/vnd.ms-excel.template.macroEnabled.12" },
        { "xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template" },
        //{ "xml", "text/xml" },
        { "xml", "application/vnd.ms-appx.bundlemanifest+xml" },
        { "xsd", "text/xml" },
        { "xsl", "application/xslt+xml" },
        { "xslt", "application/xslt+xml" },
        { "zip", "application/x-zip-compressed" },
        // Puedes agregar más según sea necesario.
    };

    // Diccionario de overrides predefinidos para partes conocidas.
    private static readonly Dictionary<string, string> OverrideMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Si se incluye el manifiesto, se puede generar como override o default, según la estrategia.
        // En el ejemplo de makeappx se trata a AppxManifest.xml como un archivo con default (al final aparece en el block map),
        // pero para [Content_Types].xml se suele definir override para el block map.
        { "/AppxBlockMap.xml", "application/vnd.ms-appx.blockmap+xml" },
        { "/AppxSignature.p7x", "application/vnd.ms-appx.signature" },
        { "/AppxMetadata/CodeIntegrity.cat", "application/vnd.ms-pkiseccat" }
        // Puedes agregar otros overrides si es necesario.
    };

    public static ContentTypesModel Create(IEnumerable<string> partNames, bool bundleMode)
    {
        if (partNames == null)
            throw new ArgumentNullException(nameof(partNames));

        List<DefaultContentType> defaults = [];
        List<OverrideContentType> overrides = [];
        HashSet<string> seenExtensions = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenOverrides = new(StringComparer.OrdinalIgnoreCase);

        foreach (string part in partNames)
        {
            string normalizedPart = NormalizePartName(part);

            if (OverrideMappings.TryGetValue(normalizedPart, out string? overrideContentType))
            {
                if (seenOverrides.Add(normalizedPart))
                {
                    overrides.Add(new OverrideContentType(normalizedPart, overrideContentType));
                }

                continue;
            }

            string extension = GetExtension(part);
            if (!string.IsNullOrEmpty(extension))
            {
                if (seenExtensions.Add(extension))
                {
                    if (bundleMode ? !DefaultMappingsForBundles.TryGetValue(extension, out var contentType) : !DefaultMappings.TryGetValue(extension, out contentType))
                    {
                        contentType = "application/octet-stream";
                    }

                    defaults.Add(new DefaultContentType(extension, contentType));
                }

                continue;
            }

            if (seenOverrides.Add(normalizedPart))
            {
                overrides.Add(new OverrideContentType(normalizedPart, "application/octet-stream"));
            }
        }

        return new ContentTypesModel([.. defaults], [.. overrides]);
    }

    private static string NormalizePartName(string part)
    {
        return part.StartsWith("/") ? part : "/" + part.Replace('\\', '/');
    }

    private static string GetExtension(string partName)
    {
        string ext = Path.GetExtension(partName);
        return string.IsNullOrEmpty(ext) ? string.Empty : ext.TrimStart('.').ToLowerInvariant();
    }
}