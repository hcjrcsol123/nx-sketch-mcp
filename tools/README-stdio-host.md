# STDIO接入

入口tools/mcp_stdio.py；Python3.14+标准库，-B禁止缓存；UTF-8 JSONL，stdout仅协议，诊断走stderr。客户端args为-B、入口绝对路径、--config、配置绝对路径。config/提供模板，连接字段pipe_name、token_file、helper_path、timeout_seconds，不包含token内容。

Entry未加载/token不存在返回BRIDGE_NOT_READY；超时不表示NX操作已取消。未知写入结果不得重放。能力、加载、所有权及检查方法见[项目说明](../README.md)。diagnose-m6.py --online只查询status/ping。
