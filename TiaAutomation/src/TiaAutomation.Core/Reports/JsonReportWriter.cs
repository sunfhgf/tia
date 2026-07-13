using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaAutomation.Core.Reports
{
    public class JsonReportWriter
    {
        public void Write(string path, object report)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var json = serializer.Serialize(report);
            File.WriteAllText(path, PrettyPrint(json), Encoding.UTF8);
        }

        private static string PrettyPrint(string json)
        {
            var output = new StringBuilder();
            var quote = false;
            var depth = 0;

            for (var i = 0; i < json.Length; i++)
            {
                var ch = json[i];
                var escaped = i > 0 && json[i - 1] == '\\';

                if (ch == '"' && !escaped)
                {
                    quote = !quote;
                }

                if (quote)
                {
                    output.Append(ch);
                    continue;
                }

                switch (ch)
                {
                    case '{':
                    case '[':
                        output.Append(ch).AppendLine();
                        output.Append(new string(' ', ++depth * 2));
                        break;
                    case '}':
                    case ']':
                        output.AppendLine();
                        output.Append(new string(' ', --depth * 2)).Append(ch);
                        break;
                    case ',':
                        output.Append(ch).AppendLine();
                        output.Append(new string(' ', depth * 2));
                        break;
                    case ':':
                        output.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(ch))
                        {
                            output.Append(ch);
                        }
                        break;
                }
            }

            return output.ToString();
        }
    }
}
