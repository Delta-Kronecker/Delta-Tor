export namespace main {
	
	export class BridgeInfo {
	    category: string;
	    transport: string;
	    ip: string;
	    filename: string;
	    count: number;
	    updated: string;
	    url: string;
	
	    static createFrom(source: any = {}) {
	        return new BridgeInfo(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.category = source["category"];
	        this.transport = source["transport"];
	        this.ip = source["ip"];
	        this.filename = source["filename"];
	        this.count = source["count"];
	        this.updated = source["updated"];
	        this.url = source["url"];
	    }
	}
	export class BridgeOverview {
	    totalFiles: number;
	    totalBridges: number;
	    transports: number;
	    categories: number;
	    bridges: BridgeInfo[];
	
	    static createFrom(source: any = {}) {
	        return new BridgeOverview(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.totalFiles = source["totalFiles"];
	        this.totalBridges = source["totalBridges"];
	        this.transports = source["transports"];
	        this.categories = source["categories"];
	        this.bridges = this.convertValues(source["bridges"], BridgeInfo);
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}
	export class SlotDef {
	    label: string;
	    source: string;
	    category: string;
	    transport: string;
	    ip: string;
	    noBridge: boolean;
	    enabled: boolean;
	
	    static createFrom(source: any = {}) {
	        return new SlotDef(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.label = source["label"];
	        this.source = source["source"];
	        this.category = source["category"];
	        this.transport = source["transport"];
	        this.ip = source["ip"];
	        this.noBridge = source["noBridge"];
	        this.enabled = source["enabled"];
	    }
	}
	export class Config {
	    version: number;
	    auto_connect_timeout: number;
	    bridges_in_torrc: number;
	    shuffle_bridges: boolean;
	    dns_over_tor: boolean;
	    max_circuit_dirtiness: number;
	    new_circuit_period: number;
	    num_entry_guards: number;
	    keep_alive_enabled: boolean;
	    keep_alive_interval: number;
	    watchdog_enabled: boolean;
	    watchdog_interval: number;
	    exit_nodes_enabled: boolean;
	    exit_nodes_countries: string;
	    strict_exit_nodes: boolean;
	    auto_proxy_on_connect: boolean;
	    sni_enabled: boolean;
	    sni_host: string;
	    last_success_cat: string;
	    last_success_trans: string;
	    last_success_ip: string;
	    multi_slots: SlotDef[];
	    custom_bridges: string;
	    use_custom_bridges: boolean;
	    extract_dir?: string;
	    exp_connection_padding: boolean;
	    exp_reduced_connection_padding: boolean;
	    exp_circuit_stream_timeout: number;
	    exp_socks_timeout: number;
	    exp_safe_logging: boolean;
	    exp_avoid_disk_writes: boolean;
	    exp_hardware_accel: boolean;
	    exp_client_dns_reject_internal: boolean;
	    exp_fascist_firewall: boolean;
	    exp_firewall_ports: string;
	    exp_reachable_addresses: string;
	    exp_num_cpus: number;
	    exp_exclude_nodes: string;
	    exp_exclude_exit_nodes: string;
	    exp_use_entry_guards_as_dir_guards: boolean;
	    exp_path_bias_circ_threshold: number;
	    exp_isolate_dest_addr: boolean;
	    exp_isolate_dest_port: boolean;
	    exp_no_exit_stream_ports: string;
	
	    static createFrom(source: any = {}) {
	        return new Config(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.version = source["version"];
	        this.auto_connect_timeout = source["auto_connect_timeout"];
	        this.bridges_in_torrc = source["bridges_in_torrc"];
	        this.shuffle_bridges = source["shuffle_bridges"];
	        this.dns_over_tor = source["dns_over_tor"];
	        this.max_circuit_dirtiness = source["max_circuit_dirtiness"];
	        this.new_circuit_period = source["new_circuit_period"];
	        this.num_entry_guards = source["num_entry_guards"];
	        this.keep_alive_enabled = source["keep_alive_enabled"];
	        this.keep_alive_interval = source["keep_alive_interval"];
	        this.watchdog_enabled = source["watchdog_enabled"];
	        this.watchdog_interval = source["watchdog_interval"];
	        this.exit_nodes_enabled = source["exit_nodes_enabled"];
	        this.exit_nodes_countries = source["exit_nodes_countries"];
	        this.strict_exit_nodes = source["strict_exit_nodes"];
	        this.auto_proxy_on_connect = source["auto_proxy_on_connect"];
	        this.sni_enabled = source["sni_enabled"];
	        this.sni_host = source["sni_host"];
	        this.last_success_cat = source["last_success_cat"];
	        this.last_success_trans = source["last_success_trans"];
	        this.last_success_ip = source["last_success_ip"];
	        this.multi_slots = this.convertValues(source["multi_slots"], SlotDef);
	        this.custom_bridges = source["custom_bridges"];
	        this.use_custom_bridges = source["use_custom_bridges"];
	        this.extract_dir = source["extract_dir"];
	        this.exp_connection_padding = source["exp_connection_padding"];
	        this.exp_reduced_connection_padding = source["exp_reduced_connection_padding"];
	        this.exp_circuit_stream_timeout = source["exp_circuit_stream_timeout"];
	        this.exp_socks_timeout = source["exp_socks_timeout"];
	        this.exp_safe_logging = source["exp_safe_logging"];
	        this.exp_avoid_disk_writes = source["exp_avoid_disk_writes"];
	        this.exp_hardware_accel = source["exp_hardware_accel"];
	        this.exp_client_dns_reject_internal = source["exp_client_dns_reject_internal"];
	        this.exp_fascist_firewall = source["exp_fascist_firewall"];
	        this.exp_firewall_ports = source["exp_firewall_ports"];
	        this.exp_reachable_addresses = source["exp_reachable_addresses"];
	        this.exp_num_cpus = source["exp_num_cpus"];
	        this.exp_exclude_nodes = source["exp_exclude_nodes"];
	        this.exp_exclude_exit_nodes = source["exp_exclude_exit_nodes"];
	        this.exp_use_entry_guards_as_dir_guards = source["exp_use_entry_guards_as_dir_guards"];
	        this.exp_path_bias_circ_threshold = source["exp_path_bias_circ_threshold"];
	        this.exp_isolate_dest_addr = source["exp_isolate_dest_addr"];
	        this.exp_isolate_dest_port = source["exp_isolate_dest_port"];
	        this.exp_no_exit_stream_ports = source["exp_no_exit_stream_ports"];
	    }
	
		convertValues(a: any, classs: any, asMap: boolean = false): any {
		    if (!a) {
		        return a;
		    }
		    if (a.slice && a.map) {
		        return (a as any[]).map(elem => this.convertValues(elem, classs));
		    } else if ("object" === typeof a) {
		        if (asMap) {
		            for (const key of Object.keys(a)) {
		                a[key] = new classs(a[key]);
		            }
		            return a;
		        }
		        return new classs(a);
		    }
		    return a;
		}
	}
	export class PingResult {
	    host: string;
	    port: number;
	    ok: boolean;
	    latency: number;
	    error: string;
	    line: string;
	
	    static createFrom(source: any = {}) {
	        return new PingResult(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.host = source["host"];
	        this.port = source["port"];
	        this.ok = source["ok"];
	        this.latency = source["latency"];
	        this.error = source["error"];
	        this.line = source["line"];
	    }
	}
	
	export class SlotTrafficResult {
	    download: string;
	    upload: string;
	
	    static createFrom(source: any = {}) {
	        return new SlotTrafficResult(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.download = source["download"];
	        this.upload = source["upload"];
	    }
	}
	export class SpeedResult {
	    download: string;
	    upload: string;
	
	    static createFrom(source: any = {}) {
	        return new SpeedResult(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.download = source["download"];
	        this.upload = source["upload"];
	    }
	}
	export class TestResult {
	    ip: string;
	    country: string;
	    isTor: boolean;
	
	    static createFrom(source: any = {}) {
	        return new TestResult(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.ip = source["ip"];
	        this.country = source["country"];
	        this.isTor = source["isTor"];
	    }
	}

}

