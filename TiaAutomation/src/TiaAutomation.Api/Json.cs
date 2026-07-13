using System.Web.Script.Serialization;

namespace TiaAutomation.Api
{
    /// <summary>
    /// 复用 Core 里已经在用的 JavaScriptSerializer，避免多引一个 JSON 库。
    /// MaxJsonLength 放大；不做日期格式定制，用户配置里没有 DateTime 字段。
    /// </summary>
    public static class Json
    {
        public static string Serialize(object obj)
        {
            var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return s.Serialize(obj);
        }

        public static T Deserialize<T>(string text)
        {
            var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return s.Deserialize<T>(text);
        }
    }
}
