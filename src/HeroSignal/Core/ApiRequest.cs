using System;
using System.Collections.Generic;

namespace HeroSignal.Core
{
    /// <summary>Üretilmiş kaynak metotlarının doldurduğu istek tanımı (SDK içi).</summary>
    internal sealed class ApiRequest
    {
        public ApiRequest(string method, string path)
        {
            Method = method;
            Path = path;
        }

        public string Method { get; }
        public string Path { get; }
        public List<KeyValuePair<string, string>> PathParams { get; } = new();
        public List<KeyValuePair<string, object>> QueryParams { get; } = new();
        public List<KeyValuePair<string, object>> FormFields { get; } = new();
        public List<KeyValuePair<string, FileUpload>> FormFiles { get; } = new();
        public object? Body { get; set; }
        public bool Multipart { get; set; }
        public bool AppIdInPath { get; set; }
        public bool QueryAppId { get; set; }
        public bool BodyAppId { get; set; }
        public bool Idempotent { get; set; }

        public void PathParam(string name, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"HeroSignal: '{name}' yol parametresi boş olamaz.", name);
            }
            PathParams.Add(new KeyValuePair<string, string>(name, value));
        }

        public void Query(string name, object? value)
        {
            if (value != null) QueryParams.Add(new KeyValuePair<string, object>(name, value));
        }

        public void MultipartField(string name, object? value)
        {
            if (value != null) FormFields.Add(new KeyValuePair<string, object>(name, value));
        }

        public void MultipartFile(string name, FileUpload? file)
        {
            if (file != null) FormFiles.Add(new KeyValuePair<string, FileUpload>(name, file));
        }
    }
}
