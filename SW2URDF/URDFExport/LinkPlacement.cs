using System;
using System.Collections.Generic;
using SW2URDF.URDF;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Keeps the origins and the inertia of a link and its descendants, and puts them back when disposed. An
    /// export moves them into the frames the files are written in; the links must not keep that, or the next
    /// export would move them once more.
    /// </summary>
    internal sealed class LinkPlacement : IDisposable
    {
        private readonly List<KeyValuePair<URDFElement, URDFElement>> saved = new List<KeyValuePair<URDFElement, URDFElement>>();

        public LinkPlacement(Link link)
        {
            Save(link);
        }

        private void Save(Link link)
        {
            Save(link.Visual.Origin, new Origin(false));
            Save(link.Collision.Origin, new Origin(false));
            Save(link.Inertial.Origin, new Origin(false));
            Save(link.Inertial.Inertia, new Inertia());
            foreach (Link child in link.Children)
            {
                Save(child);
            }
        }

        private void Save(URDFElement element, URDFElement copy)
        {
            copy.SetElement(element);
            saved.Add(new KeyValuePair<URDFElement, URDFElement>(element, copy));
        }

        public void Dispose()
        {
            foreach (KeyValuePair<URDFElement, URDFElement> pair in saved)
            {
                pair.Key.SetElement(pair.Value);
            }
            saved.Clear();
        }
    }
}
