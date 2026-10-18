import { useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';

export function useSignalR(hubUrl: string = '/hubs/agent-mesh') {
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    let conn: signalR.HubConnection | null = null;
    try {
      const origin =
        typeof window !== 'undefined' && window.location?.origin?.startsWith('http')
          ? window.location.origin
          : 'http://localhost:3000';
      const resolvedUrl = hubUrl.startsWith('http') ? hubUrl : `${origin}${hubUrl}`;

      conn = new signalR.HubConnectionBuilder()
        .withUrl(resolvedUrl)
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

      conn.onreconnecting(() => setConnected(false));
      conn.onreconnected(() => setConnected(true));
      conn.onclose(() => setConnected(false));

      conn
        .start()
        .then(() => {
          setConnected(true);
          setConnection(conn);
        })
        .catch(() => {
          setConnected(false);
        });
    } catch {
      setConnected(false);
    }

    return () => {
      conn?.stop().catch(() => {});
    };
  }, [hubUrl]);

  return { connection, connected };
}
