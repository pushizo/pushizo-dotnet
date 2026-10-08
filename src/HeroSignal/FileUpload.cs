using System;
using System.IO;

namespace HeroSignal
{
    /// <summary>Multipart dosya alanı (ör. içe aktarma CSV'si). İçerik bellekte tutulur; yeniden denemeler aynı baytları gönderir.</summary>
    public sealed class FileUpload
    {
        private readonly byte[] _content;

        /// <summary>Dosya alanı oluşturur.</summary>
        /// <param name="fileName">Dosya adı.</param>
        /// <param name="content">İçerik.</param>
        /// <param name="contentType">İçerik tipi (boşsa <c>application/octet-stream</c>).</param>
        public FileUpload(string fileName, byte[] content, string? contentType = null)
        {
            FileName = string.IsNullOrEmpty(fileName) ? "upload" : fileName;
            _content = (byte[])(content ?? throw new ArgumentNullException(nameof(content))).Clone();
            ContentType = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType!;
        }

        /// <summary>Diskteki dosyayı okur.</summary>
        /// <param name="path">Dosya yolu.</param>
        /// <param name="contentType">İçerik tipi (boşsa <c>.csv</c> için <c>text/csv</c>, diğerleri için <c>application/octet-stream</c>).</param>
        public static FileUpload FromFile(string path, string? contentType = null)
        {
            var type = contentType ?? (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : null);
            return new FileUpload(Path.GetFileName(path), File.ReadAllBytes(path), type);
        }

        /// <summary>Dosya adı.</summary>
        public string FileName { get; }

        /// <summary>İçerik tipi.</summary>
        public string ContentType { get; }

        /// <summary>İçerik (kopya).</summary>
        public byte[] GetContent() => (byte[])_content.Clone();
    }
}
