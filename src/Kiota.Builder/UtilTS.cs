using System.Linq;
using Kiota.Builder.Extensions;

namespace Kiota.Builder
{
    public class UtilTS
    {
        public static string GetModelNameFromReference(string refValue)
        {
            var name = refValue.Split("/").Last();
            return ModelNameConstruction(name);
        }

        public static string ModelNameConstruction(string modelName)
        {
            var name = "";
            var arr = modelName.Split(".");
            foreach (var str in arr)
            {
                name = name + str.ToFirstCharacterUpperCase();
            }
            return name;
        }

    }
}
