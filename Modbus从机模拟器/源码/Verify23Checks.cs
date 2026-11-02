using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ModbusSimulator;
internal static class Verify23Checks
{
    private static int checks;
    private static T Field<T>(object source, string name) { return (T)source.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(source); }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static AddressCard Find(CardView view, int area, int address)
    { view.Locate(area,address); foreach(AddressCard card in view.Cards) if(card.AreaId==area&&card.Address==address)return card; throw new Exception("card missing"); }
    private static bool Contains(Control control, string text)
    { if(control.Text.Contains(text))return true; foreach(Control child in control.Controls)if(Contains(child,text))return true; return false; }
    private static void Capture(Form form,string path)
    { Application.DoEvents(); using(Bitmap bitmap=new Bitmap(form.Width,form.Height)){ form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png); } }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using(MainForm form=new MainForm())
            {
                form.Show(); Application.DoEvents(); ComboBox display=Field<ComboBox>(form,"displayMode"); display.SelectedIndex=4; Application.DoEvents();
                CardView view=form.Views.Cards;
                Check(view.Cards[0].AreaId==2&&view.Cards[9].AreaId==2&&view.Cards[10].AreaId==3,"default grouped order exhausts holding before input");
                AddressCard head=Find(view,2,0); int fixedHeight=head.Height; head.Header.PerformClick(); head.Header.PerformClick();
                Check(head.IsExpanded&&head.Height==fixedHeight&&head.Editor.Parent!=head,"header opens shared editor without card size change");
                head.NameInput.Text="压力值"; head.UnitInput.Text="kPa"; head.SaveLabel();
                Check(head.Header.Text.Contains("压力值")&&head.ValueLabel.Text.Contains("kPa"),"editable name and engineering unit update immediately");
                Check(form.Areas[2].ParsedValue(0).Contains("kPa"),"table shares custom engineering unit");
                AddressCard clean=Find(view,2,2); form.Areas[2].SetParseRule(2,new ParseRule{Unit="mA"}); clean.RefreshValue();
                form.Areas[2].SetParseRule(2,new ParseRule{Unit="A"}); clean.RefreshValue(); clean.NameInput.Text="电流";clean.SaveLabel();
                Check(clean.UnitInput.Text=="A"&&form.Areas[2].Presentation.GetUnit(2)=="A","renaming after parse unit change does not restore stale unit");
                clean.UnitInput.Text="uA";form.Areas[2].SetParseRule(2,new ParseRule{Unit="V"});clean.SaveLabel();
                Check(form.Areas[2].Presentation.GetUnit(2)=="uA","intentional unit draft survives external presentation refresh");

                form.Areas[2].SetParseRule(0,new ParseRule{DataType="Float32"});
                Check(view.Cards[0].Address==0&&view.Cards[1].Address==2,"32bit consumes one following card");
                view.Locate(2,1); AddressCard owner=view.Cards[0]; Check(owner.Address==0,"locate hidden address resolves to owning card");
                form.Areas[2].SetParseRule(0,new ParseRule{DataType="Float64"});
                Check(view.Cards[1].Address==4,"64bit consumes three following cards");
                form.Bank.Set(2,3,4321); Check(form.Bank.Get(2,3)==4321&&form.Bank.Count(2)==10,"hidden cards preserve raw protocol register memory");
                form.Areas[2].SetParseRule(0,new ParseRule()); Check(view.Cards[1].Address==1,"return to 16bit restores following cards");
                AddressCard bit=Find(view,0,0);
                Check(!Contains(bit.Editor,"解析")&&!Contains(form.Areas[0].Settings,"解析"),"coil has no parse controls in card or sidebar");
                Check(!Contains(Find(view,1,0).Editor,"解析")&&!Contains(form.Areas[1].Settings,"解析"),"discrete input has no parse controls");
                bit=Find(view,0,0); Check(bit.Height==fixedHeight&&bit.Switch.Height<=32&&Field<Control>(bit,"lamp").Width==32,"bit and word cards share fixed size with compact indicators");
                bit.Switch.Checked=true; Check(form.Bank.Get(0,0)==1,"compact switch still edits shared state");
                // 原生拖放事件走与实际拖动相同的校验及排序处理，不打开任何设备。
                head=Find(view,2,0); DataObject data=new DataObject(); data.SetData("ModbusSimulator.CardKey","2:2");
                DragEventArgs drag=new DragEventArgs(data,0,0,0,DragDropEffects.Move,DragDropEffects.None);
                typeof(Control).GetMethod("OnDragEnter",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(head,new object[]{drag});
                Check(drag.Effect==DragDropEffects.Move&&head.AllowDrop&&head.DragHandle.AllowDrop,"native drag enter accepts existing card identity");
                typeof(Control).GetMethod("OnDragDrop",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(head,new object[]{drag});
                Check(view.Cards[0].AreaId==2&&view.Cards[0].Address==2&&view.Cards[1].Address==0,"native drop moves source before target");
                Check(form.Bank.Get(2,3)==4321,"drag sorting does not move register values");
                SimulatorConfiguration saved=form.CaptureConfiguration(); string path=Path.Combine(args[0],"2.3-名称排序配置.json"); saved.Save(path);
                form.ApplyConfiguration(SimulatorConfiguration.Load(path));
                Check(view.Cards[0].Address==2&&form.Areas[2].Presentation.GetName(0)=="压力值"&&form.Areas[2].Presentation.GetUnit(0)=="kPa","configuration restores names units and custom sort");
                view.CustomOrder=new CardKey[0]; view.Rebuild(); Check(view.Cards[0].Address==0,"default order can be restored");
                ComboBox com=Field<ComboBox>(form,"com"); form.SelectTransport(1);
                foreach(object item in com.Items)
                {
                    SerialDevice device=item as SerialDevice; Check(device!=null&&device.DisplayName.StartsWith(device.PortName+":"),"COM dropdown includes device information");
                    com.SelectedItem=item; Check(form.CaptureConfiguration().Com==device.PortName,"configuration stores raw port only");
                }
                com.Text="COM99: 测试设备"; Check(form.CaptureConfiguration().Com=="COM99","manual COM description resolves raw name without opening port");
                form.SelectTransport(0);
                head=Find(view,2,0); head.NameInput.Text="压力值"; head.UnitInput.Text="kPa"; head.SaveLabel();
                form.Bank.Set(2,0,1234); form.Areas[2].SetParseRule(0,new ParseRule{Operand=0.1,Decimals=1}); head=Find(view,2,0); head.RefreshValue();
                Capture(form,Path.Combine(args[0],"2.3寄存器卡片.png"));
                Find(view,0,0); Capture(form,Path.Combine(args[0],"2.3开关量卡片.png"));
                form.Size=new Size(980,680); Application.DoEvents();
                Check(form.Areas[2].StartInput.Top<form.Areas[2].CountInput.Top,"vertical range input layout restores clear labels");
                Capture(form,Path.Combine(args[0],"2.3紧凑界面.png")); form.Close();
            }
            Console.WriteLine("TOTAL "+checks+", FAILED 0"); return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL "+ex); return 1;}
    }
}
