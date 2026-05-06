using System.Text.RegularExpressions;
using System.Linq;

namespace Server.Command.Common
{
    public class CommandSystemHelperMethods
    {
        public static void SplitCommand(string command, out string param1, out string param2)
        {
            param2 = "";
            var splittedCommand = command.Split(new[] { ' ' }, 2);
            param1 = splittedCommand[0];

            if (splittedCommand.Length > 1)
                param2 = splittedCommand[1];
        }

        public static void SplitCommandParamArray(string command, out string[] parameters)
        {
            parameters = null;
            var paramArray = Regex.Matches(command, "\"[^\"]+\"|[^ \"]+")
            .Cast<Match>()
            .Select(m => m.Value.Trim('"'))
            .ToArray();

            if (paramArray.Length > 0)
                if (!string.IsNullOrEmpty(paramArray[0]))
                    parameters = paramArray;
        }
    }
}
