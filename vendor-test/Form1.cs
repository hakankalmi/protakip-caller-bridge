using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Threading; 
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

 

namespace WindowsFormsApplication1
{
  


    public partial class Form1 : Form
    {

        private delegate void _CallerID([MarshalAs(UnmanagedType.BStr)]  string DeviceSerial, [MarshalAs(UnmanagedType.BStr)]  string Line, [MarshalAs(UnmanagedType.BStr)]  string PhoneNumber, [MarshalAs(UnmanagedType.BStr)]  string DateTime, [MarshalAs(UnmanagedType.BStr)]  string Other);
        private delegate void _Signal([MarshalAs(UnmanagedType.BStr)]    string DeviceModel, [MarshalAs(UnmanagedType.BStr)] string DeviceSerial, int Signal1, int Signal2, int Signal3, int Signal4);
        private delegate void _SetEvents(_CallerID CallerIDEvent, _Signal SignalEvent);


// Platform AnyCPU  olmak üzere  64bit veya 32bit DLL otomatik seçiliyor.
        
        [DllImport("cidshow_x64/cid.dll", EntryPoint = "SetEvents", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern void SetEvents_x64(_CallerID CallerIDEvent, _Signal SignalEvent);

        [DllImport("cidshow_x86/cid.dll", EntryPoint = "SetEvents", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern void SetEvents_x86(_CallerID CallerIDEvent, _Signal SignalEvent);

        //      [DllImport("../../../cid.dll", EntryPoint = "SetEvents", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]



        private static   void SetEvents(_CallerID CallerIDEvent, _Signal SignalEvent){

            if (IntPtr.Size == 8) {//  64-bit process
                SetEvents_x64(CallerIDEvent, SignalEvent);
            } 
            else{
                SetEvents_x86(CallerIDEvent, SignalEvent);
            }
             
        }

      

        private _CallerID CallerIDEvent1;
        private _Signal SignalEvent1;
        
        public int counter = 0;

        // Form2 removed — popUp popup not needed for this test.



        public Form1()
        {
            InitializeComponent();
        }




        private void Form1_Load(object sender, EventArgs e)
        {

            CallerIDEvent1 = new _CallerID(CallerID);
            SignalEvent1 = new _Signal(Signal);


            SetEvents(CallerIDEvent1, SignalEvent1);

        //      CallerIDEvent1("111", "121", "411", "633", "aaa"); // self test

 

        }




        private void CallerID([MarshalAs(UnmanagedType.BStr)] string DeviceSerial, [MarshalAs(UnmanagedType.BStr)]  string Line, [MarshalAs(UnmanagedType.BStr)]  string PhoneNumber, [MarshalAs(UnmanagedType.BStr)]  string DateTime, [MarshalAs(UnmanagedType.BStr)] string Other)
        {
            string temp = "";
            counter++;
            label2.Text = counter.ToString();
      
            temp = counter.ToString() + "   :  "+ PhoneNumber + "      Line: " + Line + "      Date Time: " + DateTime + "      Device Serial: " + DeviceSerial + "       " + Other;
            listBox1.Items.Add(temp);

          //  textBox1.Text = temp;


            if (listBox1.Items.Count > 7)
            {
                listBox1.Items.RemoveAt(0);
            }

            // Form2 popup removed — popUp checkbox does nothing in this
            // minimal test build. The list box shows the caught call.

            
        }

        private void Signal([MarshalAs(UnmanagedType.BStr)] string DeviceModel, [MarshalAs(UnmanagedType.BStr)] string DeviceSerial, int Signal1, int Signal2, int Signal3, int Signal4)
        {
            

            string temp = "";


            temp = DeviceModel + " - " + DeviceSerial;

            if (temp.Length < 5)
            {
                temp = "No connection";  
            }

            label1.Text = temp;
 
            
                progressBar1.Value = Signal1;
                progressBar2.Value = Signal2;
                progressBar3.Value = Signal3;
                progressBar4.Value = Signal4;

                label3.Text = Signal1.ToString();
                label4.Text = Signal2.ToString();
                label5.Text = Signal3.ToString();
                label6.Text = Signal4.ToString();
 
 
        }



 


 


 

 



    }


 
 



  
}
