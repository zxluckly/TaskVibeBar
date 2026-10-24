using System.Web.Script.Serialization;

namespace VibeBar
{
    internal static class Json
    {
        private const int MaxJsonLength = 4 * 1024 * 1024;

        internal static string Serialize(object value)
        {
            JavaScriptSerializer serializer = CreateSerializer();
            return serializer.Serialize(value);
        }

        internal static T Deserialize<T>(string text)
        {
            JavaScriptSerializer serializer = CreateSerializer();
            return serializer.Deserialize<T>(text);
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxJsonLength;
            serializer.RecursionLimit = 100;
            return serializer;
        }
    }
}
