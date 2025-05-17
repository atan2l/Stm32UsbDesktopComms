using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
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
    private uint        _commandId;

    public bool CanSelectDevice => SelectedUsbDevice is not null;
    public bool CanControlLed   => _serialPort is not null;

    public MainWindowViewModel()
    {
        UsbDevices     = new ObservableCollection<string>(SerialPort.GetPortNames());
        DeviceResponse = string.Empty;

        _commandId = 1;
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
            Id = _commandId++,
            SetLed = new SetLed
            {
                On = !state
            }
        };


        // I don't like this
        using MemoryStream tempMs = new();
        Serializer.Serialize(tempMs, command);

        using MemoryStream commandMs = new();
        commandMs.Write([0xAA, (byte)(tempMs.Length & 0xff), (byte)((tempMs.Length >> 8) & 0xff)]);

        tempMs.Seek(0, SeekOrigin.Begin);
        tempMs.CopyTo(commandMs);

        commandMs.Seek(0, SeekOrigin.Begin);
        commandMs.CopyTo(_serialPort.BaseStream);

        while (_serialPort.BytesToRead == 0)
        {
            Thread.Sleep(10);
        }

        using MemoryStream responseMs = new();
        while (_serialPort.BytesToRead > 0)
        {
            responseMs.WriteByte((byte)_serialPort.ReadByte());
        }

        responseMs.Seek(0, SeekOrigin.Begin);

        var response = Serializer.Deserialize<Response>(responseMs);
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