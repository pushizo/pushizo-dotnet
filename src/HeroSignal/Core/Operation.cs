using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace HeroSignal.Core
{
    /// <summary>İç API: üretilmiş kaynak sınıflarının <see cref="HeroSignalClient"/>'a verdiği istek tanımı.</summary>
    internal sealed class Operation
    {
        internal Operation(string method, string path)
        {
            Method = method;
            Path = path;
        }

        internal string Method { get; }
        internal string Path { get; }
        internal Dictionary<string, string> PathParams { get; } = new Dictionary<string, string>();
        internal List<KeyValuePair<string, string>> QueryItems { get; } = new List<KeyValuePair<string, string>>();
        internal List<(string Name, string? Value, FileUpload? File)> Form { get; } = new List<(string, string?, FileUpload?)>();
        internal bool IsMultipart { get; private set; }
        internal bool QueryAppId { get; set; }
        internal bool BodyAppId { get; set; }
        internal bool Idempotent { get; set; }
        internal object? Body { get; set; }

        internal void PathParam(string name, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException($"HeroSignal: '{name}' yol parametresi boş olamaz.", name);
            }
            PathParams[name] = value;
        }

        /// <summary>Sorgu parametresi; null atlanır, koleksiyon tekrarlanan anahtar olur, nesneler JSON'a çevrilir.</summary>
        internal void Query(string name, object? value)
        {
            if (value is null)
            {
                return;
            }
            if (value is IEnumerable items && value is not string)
            {
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        QueryItems.Add(new KeyValuePair<string, string>(name, Scalar(item)));
                    }
                }
                return;
            }
            QueryItems.Add(new KeyValuePair<string, string>(name, Scalar(value)));
        }

        internal void FormField(string name, object? value)
        {
            IsMultipart = true;
            if (value is not null)
            {
                Form.Add((name, Scalar(value), null));
            }
        }

        internal void FormFile(string name, FileUpload? file)
        {
            IsMultipart = true;
            if (file is not null)
            {
                Form.Add((name, null, file));
            }
        }

        internal static string Scalar(object value) => value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f when value is long or int or double or float or decimal or short or byte => f.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value, value.GetType(), Json.Options),
        };
    }
}
