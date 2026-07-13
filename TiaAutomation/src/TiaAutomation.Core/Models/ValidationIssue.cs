namespace TiaAutomation.Core.Models
{
    public class ValidationIssue
    {
        public string Severity { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string Target { get; set; }

        public static ValidationIssue Error(string code, string message, string target = null)
        {
            return new ValidationIssue { Severity = "Error", Code = code, Message = message, Target = target };
        }

        public static ValidationIssue Warning(string code, string message, string target = null)
        {
            return new ValidationIssue { Severity = "Warning", Code = code, Message = message, Target = target };
        }
    }
}
