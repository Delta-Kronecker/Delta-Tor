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
	export class Config {
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
	    extract_dir?: string;
	
	    static createFrom(source: any = {}) {
	        return new Config(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
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
	        this.extract_dir = source["extract_dir"];
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

