using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
namespace OpenSeatNative {
    public static class SelfTest {
        static int count;
        static void Notify(string message, string title) {
            if (Environment.GetEnvironmentVariable("OPENSEAT_CI") == "1") Console.WriteLine(title + ": " + message);
            else MessageBox.Show(message, title);
        }
        static void Assert(bool condition,string name) { if(!condition) throw new Exception(name); count++; }
        static void Reject(Action action,string name) { bool rejected=false; try { action(); } catch(InvalidDataException) { rejected=true; } Assert(rejected,name); }
        static void Packet(uint kind,Action<IntPtr,int> test) {
            int head=Marshal.SizeOf(typeof(Native.Header)), size=head+(kind==0?24:16);
            IntPtr p=Marshal.AllocHGlobal(size);
            try { for(int i=0;i<size;i++) Marshal.WriteByte(p,i,0); Marshal.StructureToPtr(new Native.Header { Type=kind,Size=(uint)size,Device=new IntPtr(1234) },p,false); test(p,head); }
            finally { Marshal.FreeHGlobal(p); }
        }
        public static int Run() {
            string directory=Path.Combine(Path.GetTempPath(),"OpenSeat-test-"+Guid.NewGuid().ToString("N"));
            try {
                var settings=new Settings(); settings.Validate(); Assert(settings.Seats.Length==3,"three seats");
                settings.Seats[0].Mouse="device-A"; settings.Seats[1].Mouse="DEVICE-a";
                Reject(settings.Validate,"case insensitive duplicate mouse"); settings.Seats[1].Mouse="";
                settings.Seats[0].Monitor="screen-A"; settings.Seats[1].Monitor="screen-A";
                Reject(settings.Validate,"duplicate monitor"); settings.Seats[1].Monitor="";
                settings.Seats[2].Keyboard=null; Reject(settings.Validate,"null device field"); settings.Seats[2].Keyboard="";
                string file=Path.Combine(directory,"config.xml"); settings.Save(file);
                Assert(Settings.Load(file).Seats[0].Mouse=="device-A","round trip");
                settings.Seats[1].Mouse="device-B"; settings.Save(file);
                Assert(Settings.Load(file).Seats[1].Mouse=="device-B","atomic replacement");
                settings.Seats[1].Mouse="device-A"; Reject(delegate { settings.Save(file); },"bad save rejected");
                Assert(Settings.Load(file).Seats[1].Mouse=="device-B","bad save preserves file");
                Packet(0,delegate(IntPtr p,int h) {
                    Marshal.WriteInt16(p,h+4,1); Marshal.WriteInt32(p,h+12,-17); Marshal.WriteInt32(p,h+16,29);
                    InputEvent e=Native.Decode(p,(uint)(h+24));
                    Assert(e.X==-17 && e.Y==29 && e.Buttons==1 && !e.Absolute && e.Device.ToInt64()==1234,"mouse signed offsets and device handle");
                    Marshal.WriteInt16(p,h,1); Assert(Native.Decode(p,(uint)(h+24)).Absolute,"absolute mouse flag");
                    Reject(delegate { Native.Decode(p,(uint)(h+4)); },"truncated mouse rejected");
                });
                Packet(1,delegate(IntPtr p,int h) {
                    Marshal.WriteInt16(p,h+6,65); Assert(Native.Decode(p,(uint)(h+16)).KeyDown,"key make");
                    Marshal.WriteInt16(p,h+2,1); Assert(!Native.Decode(p,(uint)(h+16)).KeyDown,"key break");
                    Marshal.WriteInt16(p,h+2,0); Marshal.WriteInt16(p,h,255); Assert(!Native.Decode(p,(uint)(h+16)).KeyDown,"keyboard overrun ignored");
                    Marshal.WriteInt16(p,h,0x1D); Marshal.WriteInt16(p,h+2,2); Marshal.WriteInt16(p,h+6,0x11);
                    InputEvent rightCtrl=Native.Decode(p,(uint)(h+16));
                    Assert(rightCtrl.KeyDown && Native.KeyId(rightCtrl)==0x11D,"E0 prefixed key identity");
                });
                Assert(Native.ButtonPresses(0x0005)==2 && Native.ButtonPresses(0x0002)==0 && Native.ButtonPresses(0x0150)==3,"multi-button press count");
                var keys=new KeyTracker();
                InputEvent down=new InputEvent { Kind=1,KeyDown=true,MakeCode=30 }, up=new InputEvent { Kind=1,KeyUp=true,MakeCode=30 };
                keys.Feed(down); keys.Feed(down); keys.Feed(down); keys.Feed(up); keys.Feed(down);
                Assert(keys.Presses==2 && keys.RawEvents==4,"auto-repeat filtered");
                Assert(Native.DeviceLabel(@"\\?\HID#VID_046D&PID_C52B&MI_01&Col01#7&2a0f&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91e6bf6}")=="VID 046D · PID C52B · 接口 01","USB device label");
                Assert(Native.DeviceLabel(@"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002046d_PID&b023&Col01#8&1b2c&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91e6bf6}")=="VID 046D · PID B023","Bluetooth device label");
                var named=new Settings(); named.Seats[0].Name=new string('x',Settings.MaxNameLength+1);
                Reject(named.Validate,"overlong seat name rejected");
                Notify(count+" checks passed.\nHardware routing and desktop isolation are NOT validated.","OpenSeat self-test"); return 0;
            } catch(Exception ex) { Notify(ex.ToString(),"OpenSeat self-test FAILED"); return 1; }
            finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
        }
    }
}
