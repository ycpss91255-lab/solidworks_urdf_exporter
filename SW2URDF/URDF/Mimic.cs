using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace SW2URDF.URDF
{
    [DataContract(Name = "Mimic", Namespace = "http://schemas.datacontract.org/2004/07/SW2URDF")]
    public class Mimic : URDFElement
    {
        [DataMember]
        private readonly URDFAttribute JointNameAttribute;

        public string JointName
        {
            get => (string)JointNameAttribute.Value;
            set
            {
                if (value.GetType() == typeof(string))
                {
                    JointNameAttribute.Value = value;
                }
            }
        }

        [DataMember]
        private readonly URDFAttribute MultiplierAttribute;

        public double Multiplier
        {
            get => (double)MultiplierAttribute.Value;
            set
            {
                if (value.GetType() == typeof(double))
                {
                    MultiplierAttribute.Value = value;
                }
            }
        }

        [DataMember]
        private readonly URDFAttribute OffsetAttribute;

        public double Offset
        {
            get => (double)OffsetAttribute.Value;
            set
            {
                if (value.GetType() == typeof(double))
                {
                    OffsetAttribute.Value = value;
                }
            }
        }

        // The joint it names, found by FindTargets. Not serialised.
        private Joint target;

        public Mimic() : base("mimic", false)
        {
            JointNameAttribute = new URDFAttribute("joint", true, null);
            MultiplierAttribute = new URDFAttribute("multiplier", false, null);
            OffsetAttribute = new URDFAttribute("offset", false, null);

            Attributes.Add(JointNameAttribute);
            Attributes.Add(MultiplierAttribute);
            Attributes.Add(OffsetAttribute);
        }

        /// <summary>
        ///
        /// This Joint Position = multiplier * (other joint position) + offset
        /// </summary>
        /// <param name="mimicJointName"></param>
        /// <param name="multiplierText"></param>
        /// <param name="offsetText"></param>
        public void Update(string mimicJointName, string multiplierText, string offsetText)
        {
            JointName = mimicJointName;
            MultiplierAttribute.SetDoubleValueFromString(multiplierText);
            OffsetAttribute.SetDoubleValueFromString(offsetText);
        }

        public void Clear()
        {
            JointNameAttribute.Value = null;
            MultiplierAttribute.Value = null;
            OffsetAttribute.Value = null;
        }

        /// <summary>
        /// Finds the joint that every mimic names, so that the mimic follows when Joint.Update renames it.
        /// While several joints have the same name, a mimic stays with the one it named before.
        /// </summary>
        public static void FindTargets(ICollection<Joint> joints)
        {
            foreach (Mimic mimic in joints.Select(joint => joint.Mimic).Where(mimic => mimic != null))
            {
                if (mimic.JointName == null)
                {
                    mimic.target = null;
                }
                else if (mimic.target?.Name != mimic.JointName || !joints.Contains(mimic.target))
                {
                    List<Joint> named = joints.Where(joint => joint.Name == mimic.JointName).ToList();
                    mimic.target = named.Count == 1 ? named[0] : null;
                }
            }
            foreach (Joint joint in joints)
            {
                joint.MimickedBy = joints.Select(other => other.Mimic)
                    .Where(mimic => mimic != null && mimic.target == joint).ToList();
            }
        }

        internal void FillBoxes(TextBox textBoxMimicMultiplier, TextBox textBoxMimicOffset)
        {
            textBoxMimicMultiplier.Text = MultiplierAttribute.GetTextFromDoubleValue();
            textBoxMimicOffset.Text = OffsetAttribute.GetTextFromDoubleValue();
        }
    }
}