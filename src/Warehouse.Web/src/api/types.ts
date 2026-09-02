/** Mirrors the server enums, which travel as names rather than numbers. */
export type DocumentType = 'Inward' | 'Outward'

export type DocumentStatus =
  | 'Draft'
  | 'Released'
  | 'InProgress'
  | 'Completed'
  | 'Cancelled'
  | 'Failed'

export type GateState =
  | 'Idle'
  | 'Ready'
  | 'WaitingForGate'
  | 'Reading'
  | 'Processing'
  | 'Validating'
  | 'Passed'
  | 'Alarm'
  | 'Error'
  | 'ReaderDisconnected'

export type AlarmType =
  | 'UnknownEpc'
  | 'UnexpectedEpc'
  | 'MissingEpc'
  | 'NoEpc'
  | 'DocumentMismatch'
  | 'ReaderError'
  | 'GpioError'
  | 'ReaderDisconnected'
  | 'Timeout'
  | 'DuplicateGateEvent'

export type AlarmStatus = 'Active' | 'Acknowledged' | 'Resolved'

export type EpcClassification = 'Expected' | 'Unknown' | 'Unexpected' | 'Missing'

export interface GateSnapshot {
  gateCode: string
  gateName: string
  state: GateState
  readerOnline: boolean
  readerId?: string | null
  cycleId?: string | null
  cycleStartedAt?: string | null
  documentNumber?: string | null
  movementType?: DocumentType | null
  userDisplayName?: string | null
  expectedArticles: number
  detectedArticles: number
  balanceArticles: number
  expectedQuantity: number
  detectedQuantity: number
  balanceQuantity: number
  cycleDetectedCount: number
  balanceEpcs: string[]
  lastEpc?: string | null
  statusMessage?: string | null
  activeAlarm?: AlarmType | null
  timestamp: string
}

export interface EpcDetectedUpdate {
  gateCode: string
  cycleId: string
  epc: string
  isKnown: boolean
  isExpected: boolean
  detectedCount: number
  expectedCount: number
  rssi?: number | null
  antenna?: number | null
  timestamp: string
}

export interface CycleCompletedUpdate {
  gateCode: string
  cycleId: string
  passed: boolean
  documentNumber?: string | null
  documentStatus?: DocumentStatus | null
  expectedCount: number
  detectedCount: number
  missing: string[]
  unknown: string[]
  unexpected: string[]
  summary: string
  timestamp: string
}

export interface AlarmRaisedUpdate {
  alarmId: string
  alarmType: AlarmType
  gateCode?: string | null
  documentNumber?: string | null
  cycleId?: string | null
  message: string
  epc?: string | null
  epcs: string[]
  timestamp: string
}

export interface DocumentItem {
  epc: string
  itemCode?: string | null
  itemName?: string | null
  cartonNumber?: string | null
  quantity: number
  isDetected: boolean
  detectedAt?: string | null
}

export interface DocumentSummary {
  id: number
  documentNumber: string
  type: DocumentType
  status: DocumentStatus
  userDisplayName?: string | null
  gateCode?: string | null
  expectedArticles: number
  detectedArticles: number
  balanceArticles: number
  expectedQuantity: number
  detectedQuantity: number
  balanceQuantity: number
  reference?: string | null
  retryCount: number
  createdAt: string
  completedAt?: string | null
}

export interface DocumentDetail extends DocumentSummary {
  notes?: string | null
  items: DocumentItem[]
  detectedEpcs: string[]
  balanceEpcs: string[]
  releasedAt?: string | null
  cancelledAt?: string | null
  cancelledReason?: string | null
}

export interface Reader {
  readerId: string
  name: string
  gateCode: string
  ipAddress?: string | null
  port?: number | null
  model: string
  state: string
  isOnline: boolean
  isInventorying: boolean
  firmwareVersion?: string | null
  hardwareVersion?: string | null
  temperatureCelsius?: number | null
  antennas: number[]
  gpio: string[]
  lastSeenAt?: string | null
  connectedAt?: string | null
  lastError?: string | null
}

export interface Alarm {
  id: number
  alarmId: string
  alarmType: AlarmType
  status: AlarmStatus
  message: string
  gateCode?: string | null
  documentNumber?: string | null
  cycleId?: string | null
  epc?: string | null
  epcs: string[]
  raisedAt: string
  resolvedBy?: string | null
  resolvedAt?: string | null
  resolutionNotes?: string | null
}

export interface GateCycle {
  id: number
  cycleId: string
  gateCode: string
  documentNumber?: string | null
  status: string
  startedAt: string
  completedAt?: string | null
  expectedEpcCount: number
  detectedEpcCount: number
  rawReadCount: number
  unknownEpcCount: number
  unexpectedEpcCount: number
  missingEpcCount: number
  validationResult?: 'Pass' | 'Fail' | null
  validationSummary?: string | null
  inventoryCommitted: boolean
  readerHealthy: boolean
}

export interface Dashboard {
  activeGates: number
  onlineReaders: number
  offlineReaders: number
  todayInward: number
  todayOutward: number
  pendingDocuments: number
  completedDocuments: number
  activeAlarms: number
  unknownEpcsToday: number
  totalEpcs: number
  epcsInStock: number
}

export interface Session {
  token: string
  expiresAt: string
  userName: string
  displayName: string
  roles: string[]
  mustChangePassword: boolean
}

export interface Paged<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/** Result of a catalogue import, plus any documents planned from it. */
export interface EpcImportOutcome {
  import: {
    totalRows: number
    imported: number
    updated: number
    skipped: number
    errors: { row: number; epc?: string; reason: string }[]
  }
  documents: DocumentSummary[]
}

/** An account, as the Users page shows it. */
export interface WarehouseUser {
  id: number
  userName: string
  displayName: string
  email?: string | null
  roles: string[]
  isActive: boolean
  mustChangePassword: boolean
  isLockedOut: boolean
  resetRequested: boolean
  lastLoginAt?: string | null
  createdAt: string
}
