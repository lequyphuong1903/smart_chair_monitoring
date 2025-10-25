import asyncio
import struct
from functools import reduce
import operator
import sys

from bleak import BleakScanner, BleakClient

HEART_RATE_MEASUREMENT_UUID = "65670d1e-a099-4f08-ab0d-ce2263328740"
TARGET_DEVICE_NAME = "SmartChair-RLRC"

SO_FRAME = 0x02
EO_FRAME = 0x03
PAYLOAD_LENGTH = 16
FRAME_LENGTH = 20

TCP_HOST = "127.0.0.1"
TCP_PORT = 12345
CONTROL_PORT = 12346  # New: control channel for graceful shutdown

DISCONNECT_TIMEOUT = 5  # New: wait up to 5s for a clean BLE disconnect

data_buffer = bytearray()
tcp_clients = set()
shutdown_event = asyncio.Event()  # New: shutdown trigger


async def send_to_clients(payload: bytes):
    # Iterate over a snapshot to avoid "set changed size during iteration"
    for writer in list(tcp_clients):
        try:
            writer.write(payload)
            await writer.drain()
        except Exception as e:
            print(f"Error sending to client: {e}")
            try:
                tcp_clients.remove(writer)
            except KeyError:
                pass
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass


def process_buffer():
    """Parses the data_buffer to find, validate, and decode complete frames, then sends via TCP."""
    global data_buffer
    while len(data_buffer) >= FRAME_LENGTH:
        sof_index = data_buffer.find(SO_FRAME)
        if sof_index == -1:
            return

        if sof_index > 0:
            del data_buffer[:sof_index]

        if len(data_buffer) < FRAME_LENGTH:
            return

        if data_buffer[FRAME_LENGTH - 1] != EO_FRAME:
            del data_buffer[0]
            continue

        frame = data_buffer[:FRAME_LENGTH]
        payload = frame[2:2 + PAYLOAD_LENGTH]
        checksum_received = frame[18]

        bytes_to_checksum = frame[:18]
        checksum_calculated = reduce(operator.xor, bytes_to_checksum)

        if checksum_received != checksum_calculated:
            print(f"Checksum mismatch! Received: {checksum_received}, Calculated: {checksum_calculated}. Discarding frame.")
            del data_buffer[:FRAME_LENGTH]
            continue

        asyncio.create_task(send_to_clients(payload))
        del data_buffer[:FRAME_LENGTH]


def notification_handler(sender, data):
    """Handles incoming data from BLE notifications by adding it to a buffer."""
    global data_buffer
    data_buffer.extend(data)
    process_buffer()


async def tcp_server():
    """Async TCP server that sends BLE payloads to clients."""
    async def handle_client(reader, writer):
        print(f"Client connected: {writer.get_extra_info('peername')}")
        tcp_clients.add(writer)
        try:
            while not shutdown_event.is_set():
                await asyncio.sleep(0.5)
        except asyncio.CancelledError:
            pass
        finally:
            print("Client disconnected")
            try:
                tcp_clients.remove(writer)
            except KeyError:
                pass
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass

    server = await asyncio.start_server(handle_client, TCP_HOST, TCP_PORT)
    addrs = ', '.join(str(sock.getsockname()) for sock in server.sockets)
    print(f"TCP server listening on: {addrs}")
    try:
        async with server:
            await server.serve_forever()
    except asyncio.CancelledError:
        pass


async def control_server():
    """Control server to accept 'SHUTDOWN' command and gracefully stop the backend."""
    async def handle_control(reader, writer):
        try:
            data = await reader.read(1024)
            message = data.decode(errors="ignore").strip().lower()
            if message == "shutdown":
                writer.write(b"OK\n")
                await writer.drain()
                shutdown_event.set()
            else:
                writer.write(b"UNKNOWN\n")
                await writer.drain()
        except Exception as e:
            print(f"Control error: {e}")
        finally:
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass

    server = await asyncio.start_server(handle_control, TCP_HOST, CONTROL_PORT)
    addrs = ', '.join(str(sock.getsockname()) for sock in server.sockets)
    print(f"Control server listening on: {addrs} (send 'SHUTDOWN')")
    try:
        async with server:
            await server.serve_forever()
    except asyncio.CancelledError:
        pass


async def _disconnect_ble(client: BleakClient, notify_uuid: str, notifying: bool):
    """Stop notify (if started) and ensure clean BLE disconnect with timeout."""
    try:
        if notifying:
            try:
                await client.stop_notify(notify_uuid)
            except Exception as e:
                print(f"stop_notify failed: {e}")
        if client.is_connected:
            print("Disconnecting BLE...")
            try:
                await client.disconnect()
            except Exception as e:
                print(f"disconnect() raised: {e}")

            # Wait until actually disconnected (with timeout)
            try:
                await asyncio.wait_for(_wait_until(lambda: not client.is_connected), timeout=DISCONNECT_TIMEOUT)
            except asyncio.TimeoutError:
                print("BLE disconnect timeout; device might remain busy briefly.")
    except Exception as e:
        print(f"BLE cleanup error: {e}")


async def _wait_until(predicate, interval: float = 0.1):
    """Polls predicate until it returns True."""
    while not predicate():
        await asyncio.sleep(interval)


async def main():
    """Scans for, connects to, and reads data from a BLE device; also starts TCP + control servers."""
    print("Starting servers...")
    tcp_task = asyncio.create_task(tcp_server())
    control_task = asyncio.create_task(control_server())

    client: BleakClient | None = None
    notifying = False

    try:
        print(f"Scanning for a device named '{TARGET_DEVICE_NAME}'...")
        device = await BleakScanner.find_device_by_name(TARGET_DEVICE_NAME)

        if device is None:
            print(f"Could not find device '{TARGET_DEVICE_NAME}'. Waiting for shutdown command...")
            await shutdown_event.wait()
            return

        print(f"Found device: {device.name} ({device.address})")

        client = BleakClient(device)

        try:
            await client.connect()
            if not client.is_connected:
                print(f"Failed to connect to {device.address}. Waiting for shutdown command...")
                await shutdown_event.wait()
                return

            print(f"Connected to {device.address}")
            try:
                print(f"Subscribing to notifications for UUID: {HEART_RATE_MEASUREMENT_UUID}")
                await client.start_notify(HEART_RATE_MEASUREMENT_UUID, notification_handler)
                notifying = True

                print("Listening for notifications. Send SHUTDOWN to stop.")
                await shutdown_event.wait()

            except asyncio.CancelledError:
                print("Cancellation requested.")
            except Exception as e:
                print(f"Error: {e}")
            finally:
                await _disconnect_ble(client, HEART_RATE_MEASUREMENT_UUID, notifying)

        finally:
            # Ensure BLE is disconnected even if connection phase failed midway
            if client is not None and client.is_connected:
                await _disconnect_ble(client, HEART_RATE_MEASUREMENT_UUID, notifying)

    finally:
        # Stop servers
        for t in (tcp_task, control_task):
            if not t.done():
                t.cancel()
        for t in (tcp_task, control_task):
            try:
                await t
            except asyncio.CancelledError:
                pass

        # Close any remaining TCP client connections
        for writer in list(tcp_clients):
            try:
                tcp_clients.remove(writer)
            except KeyError:
                pass
            writer.close()
            try:
                await writer.wait_closed()
            except Exception:
                pass


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("Program stopped by user.")