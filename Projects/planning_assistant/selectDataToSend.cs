using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace Reflexion_assistant
{
    public partial class selectDataToSend : Form
    {
        public bool confirm = false;
        public StructureSet selectedSS = null;
        public bool exportCT = false;
        public selectDataToSend(List<StructureSet> ssList, StructureSet SSinContext)
        {
            InitializeComponent();
            foreach(StructureSet ss in ssList) RTStruct.Items.Add(ss.Id);
            if (SSinContext != null) RTStruct.Text = SSinContext.Id;
            else RTStruct.Text = ssList.First().Id;
        }

        private void export_Click(object sender, EventArgs e)
        {
            confirm = true;
            this.Close();
        }

        private void cancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
