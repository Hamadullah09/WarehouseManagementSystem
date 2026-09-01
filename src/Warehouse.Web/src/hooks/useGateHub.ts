import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { HubConnection } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'
import { session } from '../api/client'
import type {
  AlarmRaisedUpdate,
  CycleCompletedUpdate,
  EpcDetectedUpdate,
  GateSnapshot,
} from '../api/types'

export interface GateHubHandlers {
  onGateStatus?: (update: GateSnapshot) => void
  onEpcDetected?: (update: EpcDetectedUpdate) => void
  onCycleCompleted?: (update: CycleCompletedUpdate) => void
  onAlarmRaised?: (update: AlarmRaisedUpdate) => void
}

/**
 * Subscribes to the gate hub for one gate, or to the dashboard feed.
 *
 * The display never polls: the server pushes every EPC, state change and
 * verdict. SignalR reconnects on its own, and on each reconnect the hub is
 * re-joined so the screen resynchronises from a fresh snapshot rather than
 * carrying stale counters forward.
 */
export function useGateHub(gateCode: string | null, handlers: GateHubHandlers) {
  const [connected, setConnected] = useState(false)
  const handlersRef = useRef(handlers)
  handlersRef.current = handlers

  useEffect(() => {
    const token = session.get()?.token
    if (!token) return

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`/hubs/gate?access_token=${encodeURIComponent(token)}`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('GateStatusChanged', (u: GateSnapshot) => handlersRef.current.onGateStatus?.(u))
    connection.on('EpcDetected', (u: EpcDetectedUpdate) => handlersRef.current.onEpcDetected?.(u))
    connection.on('CycleCompleted', (u: CycleCompletedUpdate) =>
      handlersRef.current.onCycleCompleted?.(u),
    )
    connection.on('AlarmRaised', (u: AlarmRaisedUpdate) => handlersRef.current.onAlarmRaised?.(u))
    connection.on('ReaderStatusChanged', () => {
      /* reflected in the next gate status push */
    })

    const join = async () => {
      const snapshot = gateCode
        ? await connection.invoke<GateSnapshot>('JoinGate', gateCode)
        : null

      if (!gateCode) await connection.invoke('JoinDashboard')
      if (snapshot) handlersRef.current.onGateStatus?.(snapshot)
    }

    connection.onreconnected(() => {
      setConnected(true)
      void join()
    })

    connection.onreconnecting(() => setConnected(false))
    connection.onclose(() => setConnected(false))

    let cancelled = false

    connection
      .start()
      .then(async () => {
        if (cancelled) return
        setConnected(true)
        await join()
      })
      .catch(() => setConnected(false))

    return () => {
      cancelled = true

      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop()
      }
    }
  }, [gateCode])

  return { connected }
}
