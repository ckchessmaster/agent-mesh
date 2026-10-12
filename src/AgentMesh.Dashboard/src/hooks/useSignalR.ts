import { useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';

export function useSignalR(hubUrl: string = '/hubs/agent-mesh') {
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Information)
      .build();

    conn.onreconnecting(() => setConnected(false));
    conn.onreconnected(() => setConnected(true));
    conn.onclose(() => setConnected(false));

    conn.start()
      .then(() => {
        setConnected(true);
        setConnection(conn);
      })
      .catch((err) => {
        console.warn('SignalR Connection Error (will retry):', err);
        setConnected(false);
      });

    return () => {
      conn.stop();
    };
  }, [hubUrl]);

  return { connection, connected };
}
