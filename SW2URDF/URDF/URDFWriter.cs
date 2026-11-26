using System.Text;
using System.Xml;

namespace SW2URDF.URDF
{
    //Initiates the XMLWriter and its necessary settings
    public class URDFWriter
    {
        public XmlWriter writer;

        public URDFWriter(string savePath)
        {
            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineOnAttributes = true,
                // A file that could not be written completely must not be closed into a complete one
                WriteEndDocumentOnClose = false,
            };
            writer = XmlWriter.Create(savePath, settings);
        }
    }
}