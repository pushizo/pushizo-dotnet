using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeroSignal.Core
{
    /// <summary>SDK genelinde paylaşılan System.Text.Json ayarları: null alanlar gönderilmez, bilinmeyen alanlar yok sayılır.</summary>
    public static class Json
    {
        /// <summary>Serileştirme seçenekleri.</summary>
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
        };

        /// <summary>Hata ayıklama için JSON gösterimi (model <c>ToString</c>).</summary>
        public static string Stringify(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
    }
}
