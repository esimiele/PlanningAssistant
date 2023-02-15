using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using System.IO;
using Microsoft.Win32;
using System.Windows.Media.Media3D;
using EvilDICOM.Core;
using EvilDICOM.Core.Helpers;
using iTextSharp.text;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;


namespace Reflexion_assistant
{
    public partial class UI : UserControl
    {
        Patient pat;
        StructureSet selectedSS;
        ScriptContext context;
        Structure body = null;
        Structure couch = null;
        List<Tuple<double, double>> safetyZone = new List<Tuple<double, double>>
        {
            new Tuple<double, double>(-95, 0),
            new Tuple<double, double>(-92, 50),
            new Tuple<double, double>(-85, 100),
            new Tuple<double, double>(-72, 126),
            new Tuple<double, double>(-60, 150),
            new Tuple<double, double>(-45, 170),
            new Tuple<double, double>(-30, 185),
            new Tuple<double, double>(-20, 190),
            new Tuple<double, double>(0, 190),
            new Tuple<double, double>(20, 190),
            new Tuple<double, double>(30, 185),
            new Tuple<double, double>(45, 170),
            new Tuple<double, double>(60, 150),
            new Tuple<double, double>(72, 126),
            new Tuple<double, double>(85, 100),
            new Tuple<double, double>(92, 50),
            new Tuple<double, double>(95, 0),
        };
        double invert = 1.0;
        List<double> depths = new List<double> { 15.0, 50.0, 100.0, 150.0, 200.0 };
        double OFdepth = 100.0; //output factor depth in mm
        string path = "";

        public UI(ScriptContext sc)
        {
            InitializeComponent();
            context = sc;
            pat = context.Patient;
            selectedSS = context.StructureSet;
            //need to invert the safety zone as ant-post direction of the patient has changed sign
            if (selectedSS.Image.ImagingOrientation == PatientOrientation.HeadFirstProne || selectedSS.Image.ImagingOrientation == PatientOrientation.FeetFirstProne) { invert = -1.0; invertSafetyZone(); }

            //get body structure (logic to account for the situation where there might be multiple structures with 'body' in the Id)
            List<Structure> bodies = selectedSS.Structures.Where(x => x.Id.ToLower().Contains("body")).ToList();
            if (!bodies.Any()) MessageBox.Show("Warning! No structure found with the string 'body'! It is unclear which structure is the body! You will NOT be able to check the structure set or get dose profiles!");
            else if (bodies.Count() > 1)
            {
                Reflexion_assistant.selectItem select = new Reflexion_assistant.selectItem();
                select.message.Text = "Multiple structures found with the\nstring 'body' in the Structure set!\nPlease select a body structure!";
                foreach (Structure s in bodies) select.comboBox1.Items.Add(s.Id);
                select.comboBox1.Text = bodies.First().Id;
                select.ShowDialog();
                if (!select.confirm) return;
                body = bodies.First(x => x.Id == select.comboBox1.SelectedItem.ToString());
            }
            else body = bodies.First();
        }

        private void insertCouch_Click(object sender, RoutedEventArgs e)
        {
            couch = getCouch();
            if (couch == null)
            {
                MessageBox.Show("Warning! No couch structure found! " +
                    "Insert a couch structure before replacing with Reflexion couch! (The script uses the existing couch position to know where to insert the reflexion couch)");
                return;
            }
            if (insertReflexionCouch(couch)) return;
            couchTB.Background = Brushes.ForestGreen;
            couchTB.Text = "YES";
            if (RTS_TB.Text == "YES" && isoTB.Text == "YES") MessageBox.Show("BE SURE TO APPROVE THE STRUCTURE SET BEFORE EXPORTING!!!");
        }

        private void checkRTstructBtn_Click(object sender, RoutedEventArgs e)
        {
            //is body present? If not, exit
            if (body == null)
            {
                MessageBox.Show("Error! Body structure not found! Exiting!");
                return;
            }

            //check if image has a user origin set
            if (!selectedSS.Image.HasUserOrigin) MessageBox.Show("Warning! User origin NOT defined! However, not an issue for exporting to Reflexion as Reflexion uses DICOM coordinates only!");

            //is structure set approved?
            string msg = "Error! The following structures are approved:" + Environment.NewLine;
            List<Structure> unapprovedStructures = new List<Structure> { };
            int approvedCount = 0;
            foreach (Structure s in selectedSS.Structures.Where(x => x.HasSegment && !x.IsEmpty && !x.DicomType.ToLower().Contains("support")))
            {
                if (s.ApprovalHistory.First().ApprovalStatus == StructureApprovalStatus.Approved)
                {
                    msg += s.Id + Environment.NewLine;
                    approvedCount++;
                }
                else unapprovedStructures.Add(s);
            }
            if (approvedCount > 0)
            {
                msg += Environment.NewLine + "I can't operate on approved structures!" + Environment.NewLine + "Continue ?!";
                confirmUI CUI = new confirmUI();
                CUI.message.Text = msg;
                CUI.confirmBtn.Text = "Yes";
                CUI.cancelBtn.Text = "No";
                CUI.ShowDialog();
                if (!CUI.confirm) return;
            }

            //check to see if any structures are outside body
            List<Structure> highRes = new List<Structure> { };
            string highResMsg = "The following structures are high resolution and cannot be operated on within ESAPI:" + Environment.NewLine;
            foreach (Structure s in unapprovedStructures)
            {
                if (s.IsHighResolution)
                {
                    highResMsg += s.Id + Environment.NewLine;
                    highRes.Add(s);
                }
            }
            if (highRes.Count() > 0)
            {
                highResMsg += Environment.NewLine + "Convert to low resolution?!";
                Reflexion_assistant.confirmUI CUI = new Reflexion_assistant.confirmUI();
                CUI.message.Text = highResMsg;
                CUI.confirmBtn.Text = "Yes";
                CUI.cancelBtn.Text = "No";
                CUI.ShowDialog();
                if (CUI.confirm) convertHighResStructures(highRes);

                //remove high resolution structures from the unapproved structure list as any part of the structure extending beyond the body cannot be cropped to be within the body (different resolutions)
                unapprovedStructures.Clear();
                unapprovedStructures = selectedSS.Structures.Where(x => x.HasSegment && !x.IsHighResolution && !x.IsEmpty && !x.DicomType.ToLower().Contains("support") && x.ApprovalHistory.First().ApprovalStatus != StructureApprovalStatus.Approved).ToList();
            }

            //check to see if any structures are outside body or if any of the structures are empty
            List<Structure> outside = new List<Structure> { };
            string outsideMsg = "Error! The following structures extrude from the body structure:" + Environment.NewLine;
            foreach (Structure s in unapprovedStructures)
            {
                bool break1 = false;
                bool break2 = false;
                if (s != body && !s.Id.ToLower().Contains("couch"))
                {
                    //MessageBox.Show(s.Id);
                    MeshGeometry3D mesh = s.MeshGeometry;
                    //get the start and stop image planes for this structure
                    int startSlice = (int)((mesh.Bounds.Z - selectedSS.Image.Origin.z) / selectedSS.Image.ZRes);
                    int stopSlice = (int)(((mesh.Bounds.Z + mesh.Bounds.SizeZ) - selectedSS.Image.Origin.z) / selectedSS.Image.ZRes) + 1;

                    //string data = "";
                    for (int slice = startSlice; slice < stopSlice; slice++)
                    {
                        VVector[][] points = s.GetContoursOnImagePlane(slice);
                        for (int i = 0; i < points.GetLength(0); i++)
                        {
                            for (int j = 0; j < points[i].GetLength(0); j++)
                            {
                                if (!body.IsPointInsideSegment(points[i][j]))
                                {
                                    outsideMsg += s.Id + Environment.NewLine;
                                    outside.Add(s);
                                    break1 = true;
                                    break2 = true;
                                    break;
                                }
                            }
                            if (break1) break;
                        }
                        if (break2) break;
                    }
                }
            }
            if (outside.Count() > 0)
            {
                outsideMsg += Environment.NewLine + "Should I remove the portion of these structures outside the body?";
                Reflexion_assistant.confirmUI CUI = new Reflexion_assistant.confirmUI();
                CUI.message.Text = outsideMsg;
                CUI.confirmBtn.Text = "Yes";
                CUI.cancelBtn.Text = "No";
                CUI.ShowDialog();
                if (CUI.confirm)
                {
                    foreach (Structure s in outside)
                    {
                        //MessageBox.Show(s.Id);
                        s.SegmentVolume = s.SegmentVolume.And(body.Margin(0.0));
                    }
                }
            }

            //check if any of the structures are empty
            List<Structure> empty = new List<Structure> { };
            string emptyMsg = "The following structures are empty:" + Environment.NewLine;
            foreach (Structure s in selectedSS.Structures.Where(x => x.IsEmpty && x.ApprovalHistory.First().ApprovalStatus != StructureApprovalStatus.Approved))
            {
                if (!s.Id.ToLower().Contains("couch") && !s.Id.ToLower().Contains("rail") && !s.DicomType.ToLower().Contains("support"))
                {
                    emptyMsg += s.Id + Environment.NewLine;
                    empty.Add(s);
                }
            }
            if (empty.Count() > 0)
            {
                emptyMsg += Environment.NewLine + "Do you want me to remove them?";
                Reflexion_assistant.confirmUI CUI = new Reflexion_assistant.confirmUI();
                CUI.message.Text = emptyMsg;
                CUI.ShowDialog();
                if (CUI.confirm) foreach (Structure s in empty) if (selectedSS.CanRemoveStructure(s)) selectedSS.RemoveStructure(s);
            }

            RTS_TB.Background = Brushes.ForestGreen;
            RTS_TB.Text = "YES";
            if (isoTB.Text == "YES" && couchTB.Text == "YES") MessageBox.Show("BE SURE TO APPROVE THE STRUCTURE SET BEFORE EXPORTING!!!");
        }

        private void checkIso_Click(object sender, RoutedEventArgs e)
        {
            bool collisionLikely = false;
            if (couch == null) couch = getCouch();
            if (couch != null)
            {
                Structure theMarker = null;
                List<Structure> markers = selectedSS.Structures.Where(x => x.DicomType.ToLower().Contains("marker")).ToList();
                if (markers.Any())
                {
                    if (markers.Count() > 1)
                    {
                        Reflexion_assistant.selectItem select = new Reflexion_assistant.selectItem();
                        select.message.Text = "Multiple structures with DICOM type \n'MARKER' found! \n\nPlease select a marker!";
                        foreach (Structure s in markers) select.comboBox1.Items.Add(s.Id);
                        select.comboBox1.Text = markers.First().Id;
                        select.ShowDialog();
                        if (!select.confirm) return;
                        theMarker = markers.First(x => x.Id == select.comboBox1.SelectedItem.ToString());
                    }
                    else theMarker = markers.First();

                    //get couch and body points
                    Point3DCollection couchPts = couch.MeshGeometry.Positions;
                    Point3DCollection bodyPts = body.MeshGeometry.Positions;
                    //get lateral offset in position direction between body and couch
                    double positiveOffset = bodyPts.Max(p => p.X) - couchPts.Max(p => p.X);
                    //get lateral offset in negative direction between body and couch
                    double negativeOffset = bodyPts.Min(p => p.X) - couchPts.Min(p => p.X);

                    //these offsets are used to shift the lateral positions of the safety zone to account for the situation where the patient extends laterally off the couch
                    if (positiveOffset < 0) positiveOffset = 0;
                    if (negativeOffset > 0) negativeOffset = 0;
                    //MessageBox.Show(String.Format("{0}, {1}", positiveOffset, negativeOffset));
                    if (positiveOffset > 0 || negativeOffset < 0) updateSafetyZone(positiveOffset, negativeOffset);
                    //find the couch surface (accounting for patient orientation)
                    double couchYMin = 0.0;
                    if (selectedSS.Image.ImagingOrientation == PatientOrientation.HeadFirstProne || selectedSS.Image.ImagingOrientation == PatientOrientation.FeetFirstProne) couchYMin = couchPts.Max(p => p.Y);
                    else { couchYMin = couchPts.Min(p => p.Y);}
                    //get couch lateral
                    double couchLat = (couchPts.Max(p => p.X) + couchPts.Min(p => p.X)) / 2;
                    //find the proposed isocenter location based on the placement of the marker structure in the plan
                    double xIso = theMarker.CenterPoint.x - couchLat;
                    double yIso = couchYMin - theMarker.CenterPoint.y;

                    //iterate through the safety zone points and find the nearest neighboring points to the proposed isocenter position
                    double x1 = 0.0, y1 = 0.0;
                    double x2 = 0.0, y2 = 0.0;
                    if (yIso*invert >= 0.0 && xIso > safetyZone.ElementAt(0).Item1 && xIso < safetyZone.Last().Item1)
                    {
                        for (int i = 0; i < safetyZone.Count(); i++)
                        {
                            //lateral position of safety zone is the same as the proposed iso, but the height of the safety zone is below the proposed iso indicating the proposed iso is OUTSIDE the safety zone
                            if (safetyZone.ElementAt(i).Item1 == xIso && safetyZone.ElementAt(i).Item2 <= yIso*invert)
                            {
                                collisionLikely = true;
                                break;
                            }
                            //found nearest x points
                            else if (safetyZone.ElementAt(i).Item1 > xIso)
                            {
                                //perform linear interpolation to determine the height of the safety zone at the lateral position of the proposed isocenter. If the proposed height of the isocenter is greater than the height of 
                                //the safety zone, this indicates the proposed isocenter is OUTSIDE the safety zone --> collision likely
                                x1 = safetyZone.ElementAt(i - 1).Item1;
                                y1 = safetyZone.ElementAt(i - 1).Item2;
                                x2 = safetyZone.ElementAt(i).Item1;
                                y2 = safetyZone.ElementAt(i).Item2;
                                if (yIso*invert >= linear_interp(xIso, x1, y1, x2, y2)*invert ) { collisionLikely = true; } 
                                break;
                            }
                        }
                    }
                    else collisionLikely = true;

                    if (collisionLikely)
                    {
                        //to help the user visualize the issue if it was determined that a collision would be likely, plot the safety zone for 10 slices of the CT centered around the z location of the proposed isocenter point
                        MessageBox.Show("WARNING! COLLISION LIKELY BASED ON ISOCENTER PLACEMENT! I'm adding a structure ('Safety Zone') to indicate where the isocenter can be placed safely.");
                        Structure tmp = selectedSS.AddStructure("CONTROL", "Safety Zone");
                        //(-95, 0),
                        //(-92, 50),
                        //(-85, 100),
                        //(-72, 126),
                        //(-60, 150),
                        //(-45, 170),
                        //(-30, 185),
                        //(-20, 190),
                        //(0, 190),
                        //(20, 190),
                        //(30, 185),
                        //(45, 170),
                        //(60, 150),
                        //(72, 126),
                        //(85, 100),
                        //(92, 50),
                        //(95, 0)
                        VVector[] pts = new VVector[safetyZone.Count()];
                        for (int i = 0; i < safetyZone.Count(); i++) pts[i] = new VVector(couchLat + safetyZone.ElementAt(i).Item1, couchYMin - safetyZone.ElementAt(i).Item2, 0);
                        int startSlice = (int)((theMarker.CenterPoint.z - selectedSS.Image.Origin.z) / selectedSS.Image.ZRes) - 5;
                        for (int i = startSlice; i < startSlice + 5; i++) tmp.AddContourOnImagePlane(pts, i);
                    }
                    else { if (RTS_TB.Text == "YES" && couchTB.Text == "YES") MessageBox.Show("BE SURE TO APPROVE THE STRUCTURE SET BEFORE EXPORTING!!!"); }
                    isoTB.Background = Brushes.ForestGreen;
                    isoTB.Text = "YES";
                }
                else MessageBox.Show("Warning! No marker found in structure set! Unable to check for possible collisions based on isocenter placement!");
            }
            else MessageBox.Show("Warning! I need a couch structure to check for possible collisions based on isocenter placement!");

            // MessageBox.Show(getPDFText());
            //Reflexion_assistant.confirmUI CUI = new Reflexion_assistant.confirmUI();
            //string[] lines = getPDFText().Split(new[] {"\n" }, StringSplitOptions.None);
            //List<string> tempLines = new List<string> { };
            //string newOutput = "";
            //string temp = "";
            //bool badline = false;
            //for (int i = 0; i < lines.Length; i++)
            //{
            //    //if (i < 5) MessageBox.Show(String.Format("{0}, {1}",lines[i].IndexOf(":").ToString(), lines[i].Length-1));
            //    //newOutput += lines[i] + Environment.NewLine;
            //    if (lines[i].IndexOf(":") == lines[i].Length-1)
            //    {

            //        temp = lines[i];
            //        badline = true;
            //    }
            //    else if (badline)
            //    {
            //        temp += " " + lines[i];
            //        tempLines.Add(temp);
            //        badline = false;
            //        temp = "";
            //    }
            //    else if (lines[i].Length != 1) tempLines.Add(lines[i]);
            //}
            //List<string> fixedLines = tempLines.Where(x => x.Contains(":") && x.Contains(" ")).ToList();
            //Dictionary<string, string> reflexionTPSData = new Dictionary<string, string> { };

            ////foreach(string s in tempLines) if (s.Contains(":") && s.Contains(" ")) fixedLines.Add(s);
            //foreach (string s in fixedLines)
            //{
            //    //MessageBox.Show(s);
            //    if (!s.Contains("Approved"))
            //    {
            //        if (s.ElementAt(s.IndexOf(":") - 1).ToString() == " ") { if (!reflexionTPSData.ContainsKey(s.Substring(0, s.IndexOf(":") - 1))) reflexionTPSData.Add(s.Substring(0, s.IndexOf(":") - 1), s.Substring(s.IndexOf(":") + 2, s.Length - (s.IndexOf(":") + 2))); }
            //        else { if (!reflexionTPSData.ContainsKey(s.Substring(0, s.IndexOf(":")))) reflexionTPSData.Add(s.Substring(0, s.IndexOf(":")), s.Substring(s.IndexOf(":") + 2, s.Length - (s.IndexOf(":") + 2))); }
            //    }
            //    else
            //    {
            //        if (s.Contains("Physics Approved")) reflexionTPSData.Add("Physics approval", s.Substring(s.IndexOf(" ") + 1, s.Length - (s.IndexOf(" ") + 1)));
            //        else if(s.Contains("Quality Assurance")) reflexionTPSData.Add("QA approval", s.Substring(18, s.Length - 18));
            //        else reflexionTPSData.Add("Physician approval", s.Substring(s.IndexOf(" ") + 1, s.Length - (s.IndexOf(" ") + 1)));
            //    }
            //    //newOutput += s + Environment.NewLine;
            //}
            //foreach (KeyValuePair<string, string> itr in reflexionTPSData) newOutput += String.Format("Key: {0}, Value: {1}", itr.Key, itr.Value) + Environment.NewLine;
            ////CUI.message.Text = newOutput;
            ////CUI.ShowDialog();
            //Clipboard.SetText(newOutput);
            //MessageBox.Show("PDF text copied to clipboard");
        }

        private void updateSafetyZone(double positiveOffset, double negativeOffset)
        {
            List<Tuple<double, double>> tmp = new List<Tuple<double, double>> { };
            for (int i = 0; i < safetyZone.Count(); i++)
            {
                if (safetyZone.ElementAt(i).Item1 < 0) tmp.Add(new Tuple<double, double>(safetyZone.ElementAt(i).Item1 + positiveOffset, safetyZone.ElementAt(i).Item2));
                else if (safetyZone.ElementAt(i).Item1 > 0) tmp.Add(new Tuple<double, double>(safetyZone.ElementAt(i).Item1 + negativeOffset, safetyZone.ElementAt(i).Item2));
                else tmp.Add(new Tuple<double, double>(safetyZone.ElementAt(i).Item1, safetyZone.ElementAt(i).Item2));
            }
            safetyZone.Clear();
            safetyZone = tmp;
        }

        private void invertSafetyZone()
        {
            //flip the sign of the y-values of the safety zone
            List<Tuple<double, double>> tmp = new List<Tuple<double, double>> { };
            for (int i = 0; i < safetyZone.Count(); i++) tmp.Add(new Tuple<double, double>(safetyZone.ElementAt(i).Item1, invert*safetyZone.ElementAt(i).Item2));
            safetyZone.Clear();
            safetyZone = tmp;
        }

        private double linear_interp(double xIso, double x1, double y1, double x2, double y2)
        { return y1 + (y2 - y1) * (xIso - x1) / (x2 - x1); }

        private void convertHighResStructures(List<Structure> highResStructures)
        {
            string failMessageHeader = "Could not convert the following structures:\n";
            string failMessageBody = "";
            string successMessageHeader = "The following structures have been converted to default segmentation accuracy:\n";
            string successMessageBody = "";
            bool ranOutOfSpace = false;
            if (selectedSS.Structures.Count() >= 99) ranOutOfSpace = true;
            foreach (Structure s in highResStructures)
            {
                //create aa new Id for the original high resolution struture. The name will be '_highRes' appended to the current structure Id
                string newName = s.Id + "_highRes";
                //save the original structure Id
                string oldName = s.Id;
                if (newName.Length > 16)
                {
                    newName = newName.Substring(0, 16);
                    if (s.Id == newName) newName = s.Id.Substring(0, 11) + "_high";
                }

                try
                {
                    //update the name of the current structure
                    s.Id = newName;
                    //ensure there is space to add another structure and can you add another structure with the original structure Id
                    if (!ranOutOfSpace && s.Id != oldName && selectedSS.CanAddStructure(s.DicomType, oldName))
                    {
                        //add a new structure (default resolution by default)
                        Structure lowRes = selectedSS.AddStructure(s.DicomType, oldName);
                        //get the high res structure mesh geometry
                        MeshGeometry3D mesh = s.MeshGeometry;
                        //get the start and stop image planes for this structure
                        int startSlice = (int)((mesh.Bounds.Z - selectedSS.Image.Origin.z) / selectedSS.Image.ZRes);
                        int stopSlice = (int)(((mesh.Bounds.Z + mesh.Bounds.SizeZ) - selectedSS.Image.Origin.z) / selectedSS.Image.ZRes) + 1;

                        //foreach slice that contains contours, get the contours, and determine if you need to add or subtract the contours on the given image plane for the new low resolution structure. You need to subtract contours if the points lie INSIDE the current structure contour.
                        //We can sample three points (first, middle, and last points in array) to see if they are inside the current contour. If any of them are, subtract the set of contours from the image plane. Otherwise, add the contours to the image plane. NOTE: THIS LOGIC ASSUMES
                        //THAT YOU DO NOT OBTAIN THE CUTOUT CONTOUR POINTS BEFORE THE OUTER CONTOUR POINTS (it seems that ESAPI generally passes the main structure contours first before the cutout contours, but more testing is needed)
                        //string data = "";
                        for (int slice = startSlice; slice < stopSlice; slice++)
                        {
                            VVector[][] points = s.GetContoursOnImagePlane(slice);
                            for (int i = 0; i < points.GetLength(0); i++)
                            {
                                if (lowRes.IsPointInsideSegment(points[i][0]) || lowRes.IsPointInsideSegment(points[i][points[i].GetLength(0) - 1]) || lowRes.IsPointInsideSegment(points[i][(int)(points[i].GetLength(0) / 2)])) lowRes.SubtractContourOnImagePlane(points[i], slice);
                                else lowRes.AddContourOnImagePlane(points[i], slice);
                            }
                        }
                        if (selectedSS.Structures.Count() >= 99) ranOutOfSpace = true;
                        successMessageBody += String.Format("{0}  ---------->  {1}\n", newName, oldName);
                    }
                    else
                    {
                        //be sure to reset the structure Id to the original Id if adding the structure fails
                        if (s.DicomType == "") failMessageBody += String.Format("{0}   (DICOM type = 'None')\n", s.Id);
                        else if (s.Id == oldName) failMessageBody += String.Format("{0}   (Volume is likely approved in another structure set)\n", s.Id);
                        else
                        {
                            try { selectedSS.AddStructure(s.DicomType, oldName); }
                            catch (Exception e) { failMessageBody += String.Format("{0} (Reason: {1})\n", s.Id, e.Message); }
                        }
                        s.Id = oldName;
                    }
                }
                catch (Exception e) { failMessageBody += String.Format("{0} (Reason: {1})\n", s.Id, e.Message); }
            }
            if (failMessageBody != "")
            {
                //display the structures that couldn't be converted to the user
                string message = failMessageHeader + failMessageBody;
                if (ranOutOfSpace) message += "\nI ran out of space in the structure set! Number of structures > 98!";
                MessageBox.Show(message);
            }
            //display the structures that were converted to the user
            if (successMessageBody != "") MessageBox.Show(successMessageHeader + successMessageBody + "\nPlease review the accuracy of the generated contours!");
        }

        private Structure getCouch()
        {
            //grab an instance of the couch surface or the existing reflexion couch
            List<Structure> couches = selectedSS.Structures.Where(x => x.Id.ToLower().Contains("couchsurface")).ToList();
            if (!couches.Any()) couches = selectedSS.Structures.Where(x => x.Id.ToLower().Contains("couch reflexion")).ToList();
            if (!couches.Any()) couches = selectedSS.Structures.Where(x => x.Id.ToLower().Contains("reflexion couch")).ToList();
            if (!couches.Any()) couches = selectedSS.Structures.Where(x => x.Id.ToLower().Contains("couch")).ToList();

            if (couches.Count > 1)
            {
                Reflexion_assistant.selectItem SUI = new Reflexion_assistant.selectItem();
                SUI.message.Text = String.Format("Warning! Multiple non-empty couch structures found in plan!\n\nPlease select a structure for evaluation!");
                foreach (Structure s in couches) SUI.comboBox1.Items.Add(s.Id);
                SUI.comboBox1.Text = couches.First().Id;
                SUI.ShowDialog();
                if (!SUI.confirm) return null;
                couch = selectedSS.Structures.FirstOrDefault(x => x.Id == SUI.comboBox1.Text);
            }
            else if (couches.Count == 1) couch = couches.First();

            return couch;
        }

        private bool isPlanCalculated()
        {
            foreach (Course c in pat.Courses) if (c.ExternalPlanSetups.Where(x => x.StructureSet == selectedSS && (x.IsDoseValid || x.ApprovalStatus == PlanSetupApprovalStatus.PlanningApproved || x.ApprovalStatus == PlanSetupApprovalStatus.TreatmentApproved)).Any()) return true;
            return false;
        }

        private bool insertReflexionCouch(Structure dummy)
        {
            if (isPlanCalculated())
            {
                MessageBox.Show("Error! Couch structure(s) is used in a plan that has dose calculated or a plan that is approved! Fix this issue and try again!");
                return true;
            }
            List<Structure> existingCouches = selectedSS.Structures.Where(x => x.Id.ToLower().Contains("couch") || x.Id.ToLower().Contains("rail") || x.DicomType.ToLower().Contains("support")).ToList();
            Structure ReflexionCouch = null;
            if (selectedSS.CanAddStructure("Support", "ReflexionCouch")) ReflexionCouch = selectedSS.AddStructure("SUPPORT", "ReflexionCouch");
            else
            {
                MessageBox.Show("Error! Could not add Reflexion Couch to structure set! Exiting!");
                return true;
            }

            Point3DCollection couchPts = dummy.MeshGeometry.Positions;
            double couchYMin = 0.0;
            if (selectedSS.Image.ImagingOrientation == PatientOrientation.HeadFirstSupine || selectedSS.Image.ImagingOrientation == PatientOrientation.FeetFirstSupine) couchYMin = couchPts.Min(p => p.Y);
            else couchYMin = couchPts.Max(p => p.Y);
            double couchLat = (couchPts.Max(p => p.X) + couchPts.Min(p => p.X)) / 2;
            double couchHeight = 110.0;

            //box with contour points located at (x,y), (x,0), (x,-y), (0,-y), (-x,-y), (-x,0), (-x, y), (0,y)
            VVector[] pts = new[] {
                                    new VVector(couchLat + 264.0, couchYMin + invert*couchHeight, 0),
                                    new VVector(couchLat + 264.5, couchYMin + invert*couchHeight, 0),
                                    new VVector(couchLat + 265.0, couchYMin + invert*couchHeight, 0),
                                    new VVector(couchLat + 265.0, couchYMin + invert*(couchHeight - 0.5), 0),
                                    new VVector(couchLat + 265.0, couchYMin + invert*(couchHeight - 1.0), 0),
                                    new VVector(couchLat + 265.0, couchYMin + invert*1.0, 0),
                                    new VVector(couchLat + 265.0, couchYMin + invert*0.5, 0),
                                    new VVector(couchLat + 265.0, couchYMin, 0),
                                    new VVector(couchLat + 264.5, couchYMin, 0),
                                    new VVector(couchLat + 264.0, couchYMin, 0),
                                    new VVector(couchLat - 264.0, couchYMin, 0),
                                    new VVector(couchLat - 264.5, couchYMin, 0),
                                    new VVector(couchLat - 265.0, couchYMin, 0),
                                    new VVector(couchLat - 265.0, couchYMin + invert*0.5, 0),
                                    new VVector(couchLat - 265.0, couchYMin + invert*1.0, 0),
                                    new VVector(couchLat - 265.0, couchYMin + invert*(couchHeight - 1.0), 0),
                                    new VVector(couchLat - 265.0, couchYMin + invert*(couchHeight - 0.5), 0),
                                    new VVector(couchLat - 265.0, couchYMin + invert*couchHeight, 0),
                                    new VVector(couchLat - 264.5, couchYMin + invert*couchHeight, 0),
                                    new VVector(couchLat - 264.0, couchYMin + invert*couchHeight, 0)
            };


            foreach (Structure s in existingCouches) if (selectedSS.CanRemoveStructure(s)) selectedSS.RemoveStructure(s);
            for (int i = 0; i < selectedSS.Image.ZSize; i++) ReflexionCouch.AddContourOnImagePlane(pts, i);
            ReflexionCouch.SetAssignedHU(-1000.0);

            //check if body contour overlaps with inserted couch
            if (body == null) MessageBox.Show("Warning! Body structure NOT found! Can't check for overlap between body and inserted couch!");
            else
            {
                Structure tmp = selectedSS.AddStructure("CONTROL", "tmp");
                tmp.SegmentVolume = ReflexionCouch.And(body.Margin(0.0));
                if (!tmp.IsEmpty) MessageBox.Show("Warning!! Couch overlaps with body structure! Check your body contour!");
                selectedSS.RemoveStructure(tmp);
            }
            couch = ReflexionCouch;
            return false;
        }

        private void exportData_Click(object sender, RoutedEventArgs e)
        {
            //simple check to ensure structure set is approved before exporting to X1, otherwise the X1 will not permit optimization/import of unapproved structures
            //is structure set approved?
            bool notApproved = false;
            string msg = "Error! The following structures are not approved:" + Environment.NewLine;
            foreach (Structure s in selectedSS.Structures)
            {
                if (s.ApprovalHistory.First().ApprovalStatus != StructureApprovalStatus.Approved)
                {
                    msg += s.Id + Environment.NewLine;
                    notApproved = true;
                }
            }
            if (notApproved)
            {
                msg += "You need to approve the structure set before exporting the data!" + Environment.NewLine + "Approve the structure set and then run the script!";
                MessageBox.Show(msg);
                return;
            }

            //To be added soon... Export of the CT and structure set objects from the database via Evil DICOM
            //
            //selectDataToSend SDTS = new selectDataToSend(pat.StructureSets.ToList(), context.StructureSet);
            //SDTS.ShowDialog();
            //if (!SDTS.confirm) return;
            //bool sendCTImage = SDTS.exportCT;
            //selectedSS = SDTS.selectedSS;

            //exportTB.Background = System.Windows.Media.Brushes.ForestGreen;
            //exportTB.Text = "YES";
        }

        //static beam dose analysis
        private void getDoseProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (body == null)
            {
                MessageBox.Show("Warning! Body structure NOT found! Can't get dose profiles until a body structure is defined!");
                return;
            }
            string message = "Error! The following plans do NOT have dose calculated:" + Environment.NewLine;
            bool doseError = false;
            foreach (ExternalPlanSetup p in context.Course.ExternalPlanSetups)
            {
                if (!p.IsDoseValid)
                {
                    message += p.Id + Environment.NewLine;
                    doseError = true;
                }
            }
            if (doseError)
            {
                message += "Please calculate dose in these plans BEFORE extracting dose profiles!";
                MessageBox.Show(message);
                return;
            }

            Point3DCollection pts = body.MeshGeometry.Positions;
            Reflexion_assistant.enterData ED = new Reflexion_assistant.enterData();
            ED.depthRes.Text = "0.2";
            ED.profileRes.Text = "0.2";
            ED.ShowDialog();
            if (!ED.confirm) return;
            if (!double.TryParse(ED.depthRes.Text, out double depthres) || depthres <= 0.0)
            {
                MessageBox.Show("ERROR! ENTERED DEPTH RESOLUTION IS NOT VALID! PLEASE TRY AGAIN!");
                return;
            }
            if (!double.TryParse(ED.depthRes.Text, out double profileres) || profileres <= 0.0)
            {
                MessageBox.Show("ERROR! ENTERED PROFILE RESOLUTION IS NOT VALID! PLEASE TRY AGAIN!");
                return;
            }

            //get max/min coordinates of phantom to do profiles
            //ensure we don't get a bunch of NaN at the end of the phantom
            double yMax = pts.Max(p => p.Y) - 2.0;
            double yMin = pts.Min(p => p.Y);
            double xMax = pts.Max(p => p.X);
            double xMin = pts.Min(p => p.X);
            double zMax = pts.Max(p => p.Z);
            double zMin = pts.Min(p => p.Z);
            int numDepthPoints = (int)Math.Round((yMax - yMin) / depthres, MidpointRounding.AwayFromZero);
            int numXProfilePoints = (int)Math.Round((xMax - xMin) / profileres, MidpointRounding.AwayFromZero);
            int numZProfilePoints = (int)Math.Round((zMax - zMin) / profileres, MidpointRounding.AwayFromZero);

            //string fileName = "";
            //SaveFileDialog saveFileDialog1 = new SaveFileDialog
            //{
            //    InitialDirectory = @"\\enterprise.stanfordmed.org\depts\RadiationTherapy\Public\CancerCTR\RefleXion",
            //    //InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            //    Title = "Choose text file output",
            //    CheckPathExists = true,

            //    DefaultExt = "txt",
            //    Filter = "txt files (*.txt)|*.txt",
            //    FilterIndex = 2,
            //    RestoreDirectory = true,
            //};
            //if (saveFileDialog1.ShowDialog() == saveFileDialog1.CheckPathExists)
            //{
            //    fileName = saveFileDialog1.FileName;
            //}
            //string stringPath = "";
            //int pos1 = fileName.LastIndexOf("\\");
            //if (pos1 != -1) stringPath = fileName.Substring(0, pos1);
            //else
            //{
            //    MessageBox.Show("Path Not Found");
            //    return;
            //}

            System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();
            fbd.SelectedPath = @"\\enterprise.stanfordmed.org\depts\RadiationTherapy\Public\CancerCTR\RefleXion";
            System.Windows.Forms.DialogResult result = fbd.ShowDialog();

            if (result != System.Windows.Forms.DialogResult.OK && string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                MessageBox.Show("Path not found or path name NOT ok! Please try again!");
                return;
            }
            if (string.Equals(@"\\enterprise.stanfordmed.org\depts\RadiationTherapy\Public\CancerCTR\RefleXion", fbd.SelectedPath))
            {
                MessageBox.Show("Please write the dose profiles to another directory!");
                return;
            }
            string stringPath = fbd.SelectedPath;

            //PDD, Profiles
            foreach (ExternalPlanSetup p in context.Course.PlanSetups)
            {
                VVector isoPosition = p.Beams.First().IsocenterPosition;
                Dose d = p.Dose;
                double[] t = new double[numDepthPoints];
                DoseProfile thePDD = d.GetDoseProfile(new VVector(isoPosition.x, yMin, isoPosition.z), new VVector(isoPosition.x, yMax, isoPosition.z), t);
                string output = "PDD\n";
                output += string.Format("Start Position = ({0},{1},{2}) cm\n", isoPosition.x, yMin, isoPosition.z);
                output += string.Format("Ending Position = ({0},{1},{2}) cm\n", isoPosition.x, yMax, isoPosition.z);
                output += string.Format("Prescription dose = {0} cGy\n", p.TotalDose.Dose);
                output += "Depth (cm) Dose (%)\n";

                IEnumerator<ProfilePoint> j = thePDD.GetEnumerator();
                for (int i = 0; i < thePDD.Count; i++)
                {
                    j.MoveNext();
                    output += string.Format("{0}\t{1}\n", (j.Current.Position.y - yMin) / 10, j.Current.Value);
                }
                File.WriteAllText(string.Format(stringPath + "\\{0}_PDD.txt", p.Id), output);

                List<DoseProfile> xprofiles = new List<DoseProfile> { };
                List<DoseProfile> zprofiles = new List<DoseProfile> { };
                foreach (double depth in depths)
                {
                    double[] u = new double[numXProfilePoints];
                    DoseProfile theXPROFILE = d.GetDoseProfile(new VVector(xMax, yMin + depth, isoPosition.z), new VVector(xMin, yMin + depth, isoPosition.z), u);
                    double[] l = new double[numZProfilePoints];
                    DoseProfile theZPROFILE = d.GetDoseProfile(new VVector(isoPosition.x, yMin + depth, zMax), new VVector(isoPosition.x, yMin + depth, zMin), l);
                    xprofiles.Add(theXPROFILE);
                    zprofiles.Add(theZPROFILE);
                }

                //crosline profiles
                //file name + path, number of profile points, vector of dose profiles, is it the x profiles?, min position, max position, prescription dose, beam isocenter position
                writeProfileData(string.Format(stringPath + "\\{0}_XPROFILES.txt", p.Id), numXProfilePoints, xprofiles, true, xMin, xMax, p.TotalDose.Dose, isoPosition);

                //inline profiles
                writeProfileData(string.Format(stringPath + "\\{0}_ZPROFILES.txt", p.Id), numZProfilePoints, zprofiles, false, zMin, zMax, p.TotalDose.Dose, isoPosition);
            }
            doseProfilesTB.Background = System.Windows.Media.Brushes.ForestGreen;
            doseProfilesTB.Text = "YES";
        }

        private void writeProfileData(string file, int numDataPoints, List<DoseProfile> doseProfiles, bool profileType, double min, double max, double planDose, VVector isocenter)
        {
            //crosline profiles
            double[,] profiles = new double[numDataPoints, depths.Count()];
            double[] position = new double[numDataPoints];

            int count = 0;
            DoseProfile thePositionPROFILE = doseProfiles.First();
            IEnumerator<ProfilePoint> point = thePositionPROFILE.GetEnumerator();
            for (int i = 0; i < thePositionPROFILE.Count; i++)
            {
                point.MoveNext();
                //profileType is used to define if the profile is an x/z profile. profileType == 1 --> x profile
                position[i] = profileType ? (point.Current.Position.x) / 10 : (point.Current.Position.z) / 10;
            }
            foreach (DoseProfile thePROFILE in doseProfiles)
            {
                point = thePROFILE.GetEnumerator();
                for (int i = 0; i < thePROFILE.Count; i++)
                {
                    point.MoveNext();
                    profiles[i, count] = point.Current.Value;
                }
                count++;
            }

            string output = "";
            if (profileType)
            {
                output += "X/crossline Profiles\n";
                output += string.Format("Start Position (x,z) = ({0},{1}) cm\n", max, isocenter.z);
                output += string.Format("Ending Position (x,z) = ({0},{1}) cm\n", min, isocenter.z);
            }
            else
            {
                output += "Z/inlineline Profiles\n";
                output += string.Format("Start Position (x,z) = ({0},{1}) cm\n", isocenter.x, max);
                output += string.Format("Ending Position (x,z) = ({0},{1}) cm\n", isocenter.x, min);
            }
            output += string.Format("Prescription dose = {0} cGy\n", planDose);
            output += " \tDoses(cGy)\n";
            output += profileType ? string.Format("x_Position(cm)\t") : string.Format("z_Position(cm)\t");
            for (int depthItr = 0; depthItr < depths.Count(); depthItr++) output += depthItr == depths.Count() - 1 ? string.Format("{0}cm\n", depths.ElementAt(depthItr) / 10) : string.Format("{0}cm\t", depths.ElementAt(depthItr) / 10);
            for (int i = 0; i < doseProfiles.First().Count; i++)
            {
                //output += string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\n", xPosition[i], crosslineProfiles[i,0], crosslineProfiles[i, 1], crosslineProfiles[i, 2], crosslineProfiles[i, 3], crosslineProfiles[i, 4]);
                output += string.Format("{0}\t", position[i]);
                for (int depthItr = 0; depthItr < depths.Count(); depthItr++) output += depthItr == depths.Count() - 1 ? string.Format("{0}\n", profiles[i, depthItr]) : string.Format("{0}\t", profiles[i, depthItr]);
            }
            File.WriteAllText(file, output);
        }

        private void outputFactorBtn_Click(object sender, RoutedEventArgs e)
        {
            if (body == null)
            {
                MessageBox.Show("Warning! Body structure NOT found! Can't get output factors until a body structure is defined!");
                return;
            }
            string message = "Error! The following plans do NOT have dose calculated:" + Environment.NewLine;
            bool doseError = false;
            foreach (ExternalPlanSetup p in context.Course.ExternalPlanSetups)
            {
                if (!p.IsDoseValid)
                {
                    message += p.Id + Environment.NewLine;
                    doseError = true;
                }
            }
            if (doseError)
            {
                message += "Please calculate dose in these plans BEFORE extracting dose profiles!";
                MessageBox.Show(message);
                return;
            }

            //surface of phantom
            Point3DCollection pts = body.MeshGeometry.Positions;
            double yMin = pts.Min(p => p.Y);

            string output = "Output factors:" + Environment.NewLine;
            output += String.Format("Course: {0}", context.Course.Id) + Environment.NewLine;
            output += "PlanID Dose_at_10cm_depth_CAX" + Environment.NewLine;
            foreach (ExternalPlanSetup p in context.Course.ExternalPlanSetups)
            {
                DoseValue refPointDose = p.Dose.GetDoseToPoint(new VVector(p.Beams.First().IsocenterPosition.x, yMin + OFdepth, p.Beams.First().IsocenterPosition.z));
                if (refPointDose.IsAbsoluteDoseValue) output += String.Format("{0}, {1:0.0}\t{2}", p.Id, refPointDose.Dose, refPointDose.UnitAsString) + Environment.NewLine;
                else output += String.Format("{0}, {1:0.0}\tcGy", p.Id, refPointDose.Dose * (p.TotalDose.Dose) / 100) + Environment.NewLine;
            }

            string fileName = "";
            SaveFileDialog saveFileDialog1 = new SaveFileDialog
            {
                InitialDirectory = @"\\enterprise.stanfordmed.org\depts\RadiationTherapy\Public\CancerCTR\RefleXion",
                Title = "Choose text file output",
                CheckPathExists = true,

                DefaultExt = "txt",
                Filter = "txt files (*.txt)|*.txt",
                FilterIndex = 2,
                RestoreDirectory = true,
            };
            if (saveFileDialog1.ShowDialog() == saveFileDialog1.CheckPathExists)
            {
                fileName = saveFileDialog1.FileName;
                File.WriteAllText(fileName, output);
            }
            OF_TB.Background = System.Windows.Media.Brushes.ForestGreen;
            OF_TB.Text = "YES";
        }

        private void separateWTData_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();
            fbd.SelectedPath = @"\\enterprise.stanfordmed.org\depts\RadiationTherapy\Public\CancerCTR\RefleXion\";
            System.Windows.Forms.DialogResult result = fbd.ShowDialog();

            string[] allFiles;
            if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                allFiles = Directory.GetFiles(fbd.SelectedPath, "*.csv");
            }
            else return;
            allFiles.Count();

            if (!Directory.Exists(fbd.SelectedPath + "\\processed")) Directory.CreateDirectory(fbd.SelectedPath + "\\processed\\");

            for (int i = 0; i < allFiles.Count(); i++)
            {
                System.IO.StreamReader file = new System.IO.StreamReader(allFiles[i]);
                string line;
                int counter = 0;
                while ((line = file.ReadLine()) != null)
                {
                    if (line.ToLower().Contains("field size:"))
                    {
                        int pos = line.LastIndexOf(";");
                        //MessageBox.Show(string.Format("{0}, {1}, {2}",pos.ToString(), line.Length, line.Length - pos - 1));
                        string fileData = "";
                        //string filename = path + "processed\\";
                        string filename = fbd.SelectedPath + "\\processed\\";
                        filename += line.Substring(pos + 1, line.Length - pos - 1) + "_";
                        line = file.ReadLine();
                        line = file.ReadLine();
                        line = file.ReadLine();

                        pos = line.LastIndexOf(";");
                        if (!line.Substring(pos + 1, line.Length - pos - 1).ToLower().Contains("beam"))
                        {
                            filename += line.Substring(pos + 1, line.Length - pos - 1) + "_";
                            line = file.ReadLine();
                            line = file.ReadLine();
                            line = file.ReadLine();
                            line = file.ReadLine();
                            line = file.ReadLine();
                            //first
                            pos = line.IndexOf(";");
                            //second
                            int second = line.Substring(pos + 1, line.Length - pos - 1).IndexOf(";");
                            //third
                            int third = line.Substring(pos + second + 2, line.Length - second - pos - 2).IndexOf(";");
                            //MessageBox.Show(string.Format("{0}, {1}, {2}, {3}, {4} ", pos, pos + second + 1, pos + second + third + 2, line.Length, line.Substring(pos + second + 2, third)));
                            filename += line.Substring(pos + second + 2, third) + "mm.csv";
                        }
                        else
                        {
                            filename += "PDD.csv";
                            line = file.ReadLine();
                            line = file.ReadLine();
                            line = file.ReadLine();
                            line = file.ReadLine();
                            line = file.ReadLine();
                        }
                        fileData += line + "\n";
                        while ((line = file.ReadLine()) != "") fileData += line + "\n";
                        filename = string.Concat(filename.Where(x => !Char.IsWhiteSpace(x)));
                        File.WriteAllText(filename, fileData);

                    }
                    counter++;
                }
                file.Close();
            }
            separateWTData_TB.Background = System.Windows.Media.Brushes.ForestGreen;
            separateWTData_TB.Text = "YES";
        }

        private void importDose_Click(object sender, RoutedEventArgs e)
        {
            string reflexionFileName = "";
            OpenFileDialog openFileDialog = new OpenFileDialog();
            if (path == "") openFileDialog.InitialDirectory = @"\\enterprise.stanfordmed.org\depts\RadiationTherapy\Public\CancerCTR\RefleXion\";
            else openFileDialog.InitialDirectory = path;
            openFileDialog.Filter = "dcm files (*.dcm)|*.dcm|All files (*.*)|*.*";

            if (openFileDialog.ShowDialog().Value)
            {
                //Get the path of specified file
                reflexionFileName = openFileDialog.FileName;
                path = reflexionFileName.Substring(0, reflexionFileName.LastIndexOf("\\"));
                if (!Directory.Exists(path + "\\ESAPI_fixed\\")) Directory.CreateDirectory(path + "\\ESAPI_fixed\\");
                path += "\\ESAPI_fixed" + reflexionFileName.Substring(reflexionFileName.LastIndexOf("\\"), reflexionFileName.Length - reflexionFileName.LastIndexOf("\\") - 4) + "_fixed.dcm";

                //Read the contents of the file into a stream
                DICOMObject img = DICOMObject.Read(reflexionFileName);
                var stuff = img.FindFirst(TagHelper.ReferencedRTPlanSequence) as EvilDICOM.Core.Element.Sequence;
                List<EvilDICOM.Core.Interfaces.IDICOMElement> elements = new List<EvilDICOM.Core.Interfaces.IDICOMElement> { };
                foreach (var data in stuff.Items)
                {
                    elements = data.AllElements.Where(x => x.VR.ToString().Contains("UniqueIdentifier")).ToList();
                    if (elements.Count() > 0)
                    {
                        var newName = new EvilDICOM.Core.Element.Sequence
                        {
                            Items = new List<DICOMObject> { new DICOMObject(elements) },
                            Tag = TagHelper.ReferencedRTPlanSequence
                        };
                        img.ReplaceOrAdd(newName);
                        //img.Write(reflexionFileName.Substring(0, reflexionFileName.Length - 4) + "_fixed.dcm");
                        img.Write(path);
                        MessageBox.Show("I've finished fixing the Reflexion RTDose file! Feel free to import the _fixed file!");
                    }
                    else MessageBox.Show("Error! No dicom elements with the value representation of UniqueIdentifier in ReferencedRTPlanSequence!");
                }
            }
            else return;

            importTB.Background = System.Windows.Media.Brushes.ForestGreen;
            importTB.Text = "YES";
        }

        private void getShifts_click(object sender, RoutedEventArgs e)
        {
            string shifts = String.Format("Isoshift from CT REF:") + Environment.NewLine;
            List<VMS.TPS.Common.Model.API.Structure> markers = selectedSS.Structures.Where(d => d.DicomType.ToLower().Contains("marker") || d.DicomType.ToLower().Contains("isocenter")).ToList();
            VVector marker;
            if (!markers.Any()) { MessageBox.Show("No markers found that have the type of 'marker' or 'isocenter'. Please fix and try again!"); return; }
            else if (markers.Count > 1)
            {
                Reflexion_assistant.selectItem select = new Reflexion_assistant.selectItem();
                select.message.Text = "Multiple structures found with the\nstring 'body' in the Structure set!\nPlease select a body structure!";
                foreach (Structure s in markers) select.comboBox1.Items.Add(s.Id);
                select.comboBox1.Text = markers.First().Id;
                select.ShowDialog();
                if (!select.confirm) return;
                marker = markers.First(x => x.Id == select.comboBox1.SelectedItem.ToString()).CenterPoint;
            }
            else marker = markers.First().CenterPoint;

            VVector uOrigin = selectedSS.Image.UserOrigin;
            if (!body.IsPointInsideSegment(uOrigin)) MessageBox.Show("Warning! User origin is NOT inside the body contour!");

            //lat, long, vert shifts
            shifts += Environment.NewLine;

            //drop sign in front of shift 4-1-2021
            //4-2-2021 from some initial testing, it appears that the coordinates displayed in Eclipse are not preseved in ESAPI. I.e., a marker placed at 1,1,1 cm relative to the user origin 
            //for a HFS patient is a shift left, sup, post. A marker placed at 1,1,1 cm for a FFS patient should be a shift right, inf, post. However, after flipping the sign of the comparison for the left/right
            //sup/inf, ESAPI reported a shift of left, sup, post, which is not right. Using the original comparison sign gives the correct shift directions. Interesting...
            //4-2-2021 works for HFS and FFS patients
            List<Tuple<string, double, string>> shiftVals = new List<Tuple<string, double, string>> { };
            if (selectedSS.Image.ImagingOrientation == PatientOrientation.HeadFirstSupine)
            {
                if (marker.x - uOrigin.x > 0) shiftVals.Add(new Tuple<string, double, string>("-", marker.x - uOrigin.x, "LEFT"));
                else shiftVals.Add(new Tuple<string, double, string>("+", marker.x - uOrigin.x, "RIGHT"));

                if(marker.y - uOrigin.y > 0) shiftVals.Add(new Tuple<string, double, string>("+", marker.y - uOrigin.y, "POST"));
                else shiftVals.Add(new Tuple<string, double, string>("-", marker.y - uOrigin.y, "ANT"));

                if(marker.z - uOrigin.z > 0) shiftVals.Add(new Tuple<string, double, string>("-", marker.z - uOrigin.z, "SUP"));
                else shiftVals.Add(new Tuple<string, double, string>("+", marker.z - uOrigin.z, "INF"));
            }
            else if(selectedSS.Image.ImagingOrientation == PatientOrientation.FeetFirstSupine)
            {
                if (marker.x - uOrigin.x > 0) shiftVals.Add(new Tuple<string, double, string>("+", marker.x - uOrigin.x, "LEFT"));
                else shiftVals.Add(new Tuple<string, double, string>("-", marker.x - uOrigin.x, "RIGHT"));

                if (marker.y - uOrigin.y > 0) shiftVals.Add(new Tuple<string, double, string>("+", marker.y - uOrigin.y, "POST"));
                else shiftVals.Add(new Tuple<string, double, string>("-", marker.y - uOrigin.y, "ANT"));

                if (marker.z - uOrigin.z > 0) shiftVals.Add(new Tuple<string, double, string>("+", marker.z - uOrigin.z, "SUP"));
                else shiftVals.Add(new Tuple<string, double, string>("-", marker.z - uOrigin.z, "INF"));
            }
            else if (selectedSS.Image.ImagingOrientation == PatientOrientation.HeadFirstProne)
            {
                if (marker.x - uOrigin.x > 0) shiftVals.Add(new Tuple<string, double, string>("+", marker.x - uOrigin.x, "LEFT"));
                else shiftVals.Add(new Tuple<string, double, string>("-", marker.x - uOrigin.x, "RIGHT"));

                if (marker.y - uOrigin.y > 0) shiftVals.Add(new Tuple<string, double, string>("-", marker.y - uOrigin.y, "POST"));
                else shiftVals.Add(new Tuple<string, double, string>("+", marker.y - uOrigin.y, "ANT"));

                if (marker.z - uOrigin.z > 0) shiftVals.Add(new Tuple<string, double, string>("-", marker.z - uOrigin.z, "SUP"));
                else shiftVals.Add(new Tuple<string, double, string>("+", marker.z - uOrigin.z, "INF"));
            }
            else if (selectedSS.Image.ImagingOrientation == PatientOrientation.FeetFirstProne)
            {
                if (marker.x - uOrigin.x > 0) shiftVals.Add(new Tuple<string, double, string>("-", marker.x - uOrigin.x, "LEFT"));
                else shiftVals.Add(new Tuple<string, double, string>("+", marker.x - uOrigin.x, "RIGHT"));

                if (marker.y - uOrigin.y > 0) shiftVals.Add(new Tuple<string, double, string>("-", marker.y - uOrigin.y, "POST"));
                else shiftVals.Add(new Tuple<string, double, string>("+", marker.y - uOrigin.y, "ANT"));

                if (marker.z - uOrigin.z > 0) shiftVals.Add(new Tuple<string, double, string>("+", marker.z - uOrigin.z, "SUP"));
                else shiftVals.Add(new Tuple<string, double, string>("-", marker.z - uOrigin.z, "INF"));
            }
            else
            { MessageBox.Show("Patient imaging orientation is NOT HFS, HFP, FFS, or FFP! Cannot calculate shifts!"); return; }

            //if (Math.Abs(marker.x - uOrigin.x) >= 0.001) shifts += String.Format("X = {0}{1:0.0} mm {2}", marker.x - uOrigin.x > 0 ? "-" : "+", Math.Abs(marker.x - uOrigin.x), marker.x - uOrigin.x > 0 ? "LEFT" : "RIGHT") + Environment.NewLine;
            //if (Math.Abs(marker.z - uOrigin.z) >= 0.001) shifts += String.Format("Y = {0}{1:0.0} mm {2}", marker.z - uOrigin.z > 0 ? "-" : "+", Math.Abs(marker.z - uOrigin.z), marker.z - uOrigin.z > 0 ? "SUP" : "INF") + Environment.NewLine;
            //if (Math.Abs(marker.y - uOrigin.y) >= 0.001) shifts += String.Format("Z = {0}{1:0.0} mm {2}", marker.y - uOrigin.y > 0 ? "+" : "-", Math.Abs(marker.y - uOrigin.y), marker.y - uOrigin.y > 0 ? "POST" : "ANT") + Environment.NewLine;

            if (Math.Abs(marker.x - uOrigin.x) >= 0.001) shifts += String.Format("X = {0}{1:0.0} mm {2}", shiftVals.ElementAt(0).Item1, Math.Abs(shiftVals.ElementAt(0).Item2), shiftVals.ElementAt(0).Item3) + Environment.NewLine;
            if (Math.Abs(marker.z - uOrigin.z) >= 0.001) shifts += String.Format("Y = {0}{1:0.0} mm {2}", shiftVals.ElementAt(2).Item1, Math.Abs(shiftVals.ElementAt(2).Item2), shiftVals.ElementAt(2).Item3) + Environment.NewLine;
            if (Math.Abs(marker.y - uOrigin.y) >= 0.001) shifts += String.Format("Z = {0}{1:0.0} mm {2}", shiftVals.ElementAt(1).Item1, Math.Abs(shiftVals.ElementAt(1).Item2), shiftVals.ElementAt(1).Item3) + Environment.NewLine;

            couch = getCouch();
            if (couch != null) 
            {
                double couchMin = 0.0;
                if (selectedSS.Image.ImagingOrientation == PatientOrientation.HeadFirstProne || selectedSS.Image.ImagingOrientation == PatientOrientation.FeetFirstProne) couchMin = couch.MeshGeometry.Positions.Max(p => p.Y);
                else couchMin = couch.MeshGeometry.Positions.Min(p => p.Y);
                shifts += Environment.NewLine + String.Format("TT = {0:0.0} mm", 475.0 - Math.Abs(couchMin - marker.y)); 
            }
            else shifts += Environment.NewLine + "No couch structure found!";
            System.Windows.Clipboard.SetText(shifts);
            getShiftsTB.Background = System.Windows.Media.Brushes.ForestGreen;
            getShiftsTB.Text = "YES";
        }
    }
}
