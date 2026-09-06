# Security

The server runs locally over stdio with the permissions of its host process. It accepts only configured
input roots, rejects traversal and reparse-point paths, confines generated exports, and limits results,
sessions, batches and read sizes. These controls are not an operating-system sandbox for the parser.
Use archives you trust and folders you intentionally authorize.

AES keys are held in process memory rather than saved by the server. Your MCP client may retain tool
arguments in conversation history. Supply only keys and assets you are authorized to use. The server
does not download keys, game content or native libraries.

Please report exploitable defects through GitHub's private vulnerability reporting feature when
available. Do not post secrets, proprietary game archives or exploit payloads in public issues. For
ordinary bugs, use a public issue with a sanitized error, profile, container format and reproduction.
