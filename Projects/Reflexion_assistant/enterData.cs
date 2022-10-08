using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Reflexion_assistant
{
    public partial class enterData : Form
    {
        public bool confirm = false;
        public enterData()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            confirm = true;
            this.Close();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
