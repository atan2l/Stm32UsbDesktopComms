using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _deviceMessage;

    private SerialPort? _serialPort;
    private uint        _commandId;

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Response>> _pendingResponses;
    private          CancellationTokenSource?                                   _responseProcessorCts;
    private          Task?                                                      _responseProcessorTask;

    public bool CanSelectDevice => SelectedUsbDevice is not null;
    public bool CanControlLed   => _serialPort is not null;
    public bool CanSendMessage  => _serialPort is not null && DeviceMessage.Length > 0;

    public MainWindowViewModel()
    {
        UsbDevices     = new ObservableCollection<string>(SerialPort.GetPortNames());
        DeviceResponse = string.Empty;
        DeviceMessage  = string.Empty;

        _commandId        = 1;
        _pendingResponses = [];
    }

    [RelayCommand(CanExecute = nameof(CanSelectDevice))]
    private void SelectUsbDevice()
    {
        Debug.Assert(SelectedUsbDevice is not null);

        StopResponseProcessor();

        _serialPort?.Close();
        _serialPort = new SerialPort(SelectedUsbDevice);
        _serialPort.Open();

        StartResponseProcessor();

        ControlLedCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanControlLed))]
    private async Task ControlLed(bool state)
    {
        Debug.Assert(_serialPort is not null);

        uint commandId = _commandId++;
        Command command = new()
        {
            Id = commandId,
            SetLed = new SetLed
            {
                On = state
            }
        };

        TaskCompletionSource<Response> tcs = new();
        _pendingResponses[commandId] = tcs;

        try
        {
            await SendCommandAsync(command);

            Response response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            DeviceResponse += JsonSerializer.Serialize(response) + Environment.NewLine;
        }
        catch (TimeoutException)
        {
            DeviceResponse += $"Timeout waiting for response to command {commandId}" + Environment.NewLine;
        }
        finally
        {
            _pendingResponses.TryRemove(commandId, out _);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessage()
    {
        Debug.Assert(_serialPort is not null);

        uint commandId = _commandId++;
        Command command = new()
        {
            Id = commandId,
            PrintMessage = new PrintMessage
            {
                Message = DeviceMessage
            }
        };

        TaskCompletionSource<Response> tcs = new();
        _pendingResponses[commandId] = tcs;

        try
        {
            await SendCommandAsync(command);

            Response response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            DeviceResponse += JsonSerializer.Serialize(response) + Environment.NewLine;
        }
        catch (TimeoutException)
        {
            DeviceResponse += $"Timeout waiting for response to command {commandId}" + Environment.NewLine;
        }
        finally
        {
            _pendingResponses.TryRemove(commandId, out _);
            DeviceMessage = string.Empty;
        }
    }

    private async Task SendCommandAsync(Command command)
    {
        byte[] serialisedData;
        using (MemoryStream tempMs = new())
        {
            Serializer.Serialize(tempMs, command);
            serialisedData = tempMs.ToArray();
        }

        var frame = new byte[3 + serialisedData.Length];
        frame[0] = 0xAA;
        frame[1] = (byte)(serialisedData.Length        & 0xFF);
        frame[2] = (byte)((serialisedData.Length >> 8) & 0xFF);

        Array.Copy(
            serialisedData,
            0,
            frame,
            3,
            serialisedData.Length
        );

        await _serialPort!.BaseStream.WriteAsync(frame);
    }

    private void StartResponseProcessor()
    {
        _responseProcessorCts  = new CancellationTokenSource();
        _responseProcessorTask = Task.Run(async () => await ProcessResponsesAsync(_responseProcessorCts.Token));
    }

    private void StopResponseProcessor()
    {
        _responseProcessorCts?.Cancel();
        _responseProcessorTask?.Wait(1000);
        _responseProcessorCts?.Dispose();

        _responseProcessorCts  = null;
        _responseProcessorTask = null;

        foreach (KeyValuePair<uint, TaskCompletionSource<Response>> kvp in _pendingResponses)
        {
            kvp.Value.TrySetCanceled();
        }

        _pendingResponses.Clear();
    }

    private async Task ProcessResponsesAsync(CancellationToken cancellationToken)
    {
        const int pollIntervalMs = 10;

        while (!cancellationToken.IsCancellationRequested && _serialPort?.IsOpen == true)
        {
            try
            {
                if (_serialPort.BytesToRead > 0)
                {
                    await ProcessAvailableResponsesAsync();
                }
                else
                {
                    await Task.Delay(pollIntervalMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested.
                break;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Error processing responses: {e.Message}");
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
        }
    }

    private async Task ProcessAvailableResponsesAsync()
    {
        while (_serialPort?.BytesToRead > 0)
        {
            await using MemoryStream ms = new();
            while (_serialPort.BytesToRead > 0)
            {
                ms.WriteByte((byte)_serialPort.ReadByte());
            }

            ms.Seek(0, SeekOrigin.Begin);

            try
            {
                var response = Serializer.Deserialize<Response>(ms);
                if (_pendingResponses.TryRemove(response.Id, out TaskCompletionSource<Response>? tcs))
                {
                    tcs.SetResult(response);
                }
                else
                {
                    Debug.WriteLine($"Received unexpected response with ID {response.Id}");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Error deserialising response: {e.Message}");
            }
        }
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
