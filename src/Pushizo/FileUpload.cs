using System;
using System.IO;

namespace Pushizo
{
    /// <summary>Multipart dosya alanı. İçerik bellekte tutulur ki yeniden denemeler aynı gövdeyi göndersin.</summary>
    public sealed class FileUpload
    {
        /// <param name="filename">Dosya adı.</param>
        /// <param name="content">Dosya baytları.</param>
        /// <param name="contentType">İçerik tipi (null ise <c>application/octet-stream</c>).</param>
        public FileUpload(string filename, byte[] content, string? contentType = null)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Filename = string.IsNullOrEmpty(filename) ? "upload" : filename;
            ContentType = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType!;
        }

        public string Filename { get; }
        public byte[] Content { get; }
        public string ContentType { get; }

        /// <summary>Diskteki dosyayı okur; <c>.csv</c> için içerik tipi <c>text/csv</c> olur.</summary>
        public static FileUpload FromPath(string path, string? contentType = null)
        {
            var name = Path.GetFileName(path);
            var type = contentType ?? (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : null);
            return new FileUpload(name, File.ReadAllBytes(path), type);
        }
    }
}
