using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProtoBuf;
using Stm32Command;
using Stm32Response;

namespace Stm32UsbDesktopComms.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<string> _usbDevices;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SelectUsbDeviceCommand))]
    private string? _selectedUsbDevice;

    public bool CanSelectDevice => SelectedUsbDevice is not null;

    public MainWindowViewModel()
    {
        _usbDevices = new ObservableCollection<string>(SerialPort.GetPortNames());
    }

    [RelayCommand(CanExecute = nameof(CanSelectDevice))]
    private void SelectUsbDevice()
    {
        Debug.Assert(SelectedUsbDevice is not null);

        SerialPort sp = new(SelectedUsbDevice, 115200);
        sp.Open();

        Command c = new()
        {
            Id = 1,
            SetLed = new SetLed
            {
                On = true
            }
        };
        Serializer.Serialize(sp.BaseStream, c);

        var response = Serializer.Deserialize<Response>(sp.BaseStream);
        Debug.WriteLine(response.Status == Status.Ok);
        sp.Close();
    }
}