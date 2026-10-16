export type Provider = "codex" | "dsh";
export type Language = "zh" | "en";
export type ArchiveFilter = "all" | "active" | "archived";
export interface Session {
  id: string;
  title: string;
  path: string;
  archived: boolean;
  subagent: boolean;
  parentId?: string;
  parentTitle?: string;
  purpose?: string;
  updated: string;
  bytes: number;
  source: string;
  compressed: boolean;
}
export interface Project {
  id: string;
  title: string;
  path: string;
  sessions: Session[];
  canBackupFiles?: boolean;
}
export interface Catalog {
  provider: Provider;
  home: string;
  projects: Project[];
  warnings: string[];
}
export interface Message {
  role: "user" | "assistant" | "notice";
  time: string;
  text: string;
  truncated: boolean;
}
export interface Preview {
  revision?: string;
  navigation?: {
    index: number;
    number: number;
    question: string;
    answer: string;
  }[];
  messages: Message[];
  total: number;
  nextOffset: number;
  hasMore: boolean;
}
export interface ReadRequest {
  sessionPath?: string;
  action: "list" | "preview" | "storage";
  provider: Provider;
  home?: string;
  language: Language;
  projectId?: string;
  id?: string;
  offset?: number;
}
export interface Gateway {
  read<T>(request: ReadRequest): Promise<T>;
  operate?<T>(request: OperationRequest): Promise<T>;
}
export type OperationAction =
  | "backup"
  | "packageInfo"
  | "inspect"
  | "import"
  | "trash"
  | "restore"
  | "purge"
  | "trashProject"
  | "planDelete"
  | "delete"
  | "archive"
  | "unarchive"
  | "recover"
  | "planRepair"
  | "repair";
export interface OperationRequest {
  action: OperationAction;
  provider: Provider;
  home?: string;
  language: Language;
  id?: string;
  ids?: string[];
  projectIds?: string[];
  output?: string;
  package?: string;
  target?: string;
  mode?: "merge" | "copy";
  conflict?: "empty" | "skip" | "overwrite";
  token?: string;
  projectMode?: "none" | "recycle" | "permanent";
  includeFiles?: boolean;
  map?: boolean;
  restoreFiles?: boolean;
  permanent?: boolean;
  batch?: boolean;
  projectConfirmation?: string;
}
export interface StorageSummary {
  bytes: number;
  files: number;
  directories: number;
  skipped: number;
  error?: string;
}
export interface PackageSummary {
  raw: boolean;
  created: string;
  bytes: number;
  mainCount: number;
  subagentCount: number;
  projects: {
    title: string;
    path: string;
    targetFolder: string;
    hasFiles: boolean;
  }[];
  hasFiles: boolean;
  fileCount: number;
  fileBytes: number;
  suggestedTarget: string;
}
export function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KiB`;
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MiB`;
  return `${(bytes / 1024 ** 3).toFixed(2)} GiB`;
}
export function filterSessions(
  sessions: Session[],
  kind: "main" | "subagent",
  archive: ArchiveFilter,
  query: string,
) {
  const needle = query.trim().toLocaleLowerCase();
  return sessions.filter(
    (s) =>
      s.subagent === (kind === "subagent") &&
      (archive === "all" || s.archived === (archive === "archived")) &&
      [s.title, s.id, s.path].some((v) =>
        v.toLocaleLowerCase().includes(needle),
      ),
  );
}
