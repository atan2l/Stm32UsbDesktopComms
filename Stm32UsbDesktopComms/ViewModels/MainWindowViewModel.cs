using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProtoBuf;
using Stm32Command;
using Stm32Response;

namespace Stm32UsbDesktopComms.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private ObservableCollection<string> _usbDevices;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectUsbDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ControlLedCommand))]
    private string? _selectedUsbDevice;

    [ObservableProperty] private string _deviceResponse;

    private SerialPort? _serialPort;

    public bool CanSelectDevice => SelectedUsbDevice is not null;
    public bool CanControlLed   => _serialPort is not null;

    public MainWindowViewModel()
    {
        UsbDevices     = new ObservableCollection<string>(SerialPort.GetPortNames());
        DeviceResponse = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSelectDevice))]
    private void SelectUsbDevice()
    {
        Debug.Assert(SelectedUsbDevice is not null);

        _serialPort?.Close();
        _serialPort = new SerialPort(SelectedUsbDevice);
        _serialPort.Open();

        ControlLedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanControlLed))]
    private void ControlLed(bool state)
    {
        Debug.Assert(_serialPort is not null);

        Command command = new()
        {
            Id = 1,
            SetLed = new SetLed
            {
                On = !state
            }
        };

        Serializer.Serialize(_serialPort.BaseStream, command);

        while (_serialPort.BytesToRead == 0)
        {
            Thread.Sleep(10);
        }

        using MemoryStream ms = new();
        while (_serialPort.BytesToRead > 0)
        {
            ms.WriteByte((byte)_serialPort.ReadByte());
        }
        ms.Seek(0, SeekOrigin.Begin);

        var response = Serializer.Deserialize<Response>(ms);
        DeviceResponse += JsonSerializer.Serialize(response) + Environment.NewLine;
    }

    private void ReleaseUnmanagedResources()
    {
        // No unmanaged resources to release.
    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
        {
            _serialPort?.Close();
            _serialPort?.Dispose();
            _serialPort = null;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}