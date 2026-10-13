using System.Xml.Linq;
using WPMT.Core.Common;

namespace WPMT.Core
{
    public class ElementAttribute : IComparable<ElementAttribute>
    {
        public XNamespace Namespace;
        public string Name;
        public string Value;
        public bool IsVersion;

        public ElementAttribute(XNamespace ns, string name, string value)
        {
            Namespace = ns;
            Name = name;
            Value = value;
            IsVersion = Name.Equals(Constants.PascalCase.Version, StringComparison.OrdinalIgnoreCase);
        }

        public int CompareTo(ElementAttribute? attr)
        {
            return AttributeComparer.Instance.Compare(this, attr);
        }

        public class AttributeComparer : IComparer<ElementAttribute>
        {
            public static readonly AttributeComparer Instance = new();

            public int Compare(ElementAttribute? attrA, ElementAttribute? attrB)
            {
                if (attrA is null)
                {
                    return attrB is null ? 0 : -1;
                }
                else if (attrB is null)
                {
                    return 1;
                }

                return attrA.Namespace != attrB.Namespace ?
                    string.Compare(attrA.Namespace.NamespaceName, attrB.Namespace.NamespaceName, StringComparison.OrdinalIgnoreCase) :
                    string.Compare(attrA.Name, attrB.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}