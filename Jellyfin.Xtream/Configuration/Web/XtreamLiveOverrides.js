export default function (view) {
  const createChannelRow = (channel, overrides, epgSources) => {
    const tr = document.createElement('tr');
    tr.dataset['channelId'] = channel.Id;

    let td = document.createElement('td');
    const number = document.createElement('input');
    number.type = 'number';
    number.setAttribute('is', 'emby-input');
    number.placeholder = channel.Number;
    number.value = overrides.Number ?? '';
    number.onchange = () => number.value ?
      overrides.Number = parseInt(number.value) :
      delete overrides.Number;
    td.appendChild(number);
    tr.appendChild(td);

    td = document.createElement('td');
    const name = document.createElement('input');
    name.type = 'text';
    name.setAttribute('is', 'emby-input');
    name.placeholder = channel.Name;
    name.value = overrides.Name ?? '';
    name.onchange = () => name.value ?
      overrides.Name = name.value :
      delete overrides.Name;
    td.appendChild(name);
    tr.appendChild(td);

    td = document.createElement('td');
    const image = document.createElement('input');
    image.type = 'text';
    image.setAttribute('is', 'emby-input');
    image.placeholder = channel.LogoUrl;
    image.value = overrides.LogoUrl ?? '';
    image.onchange = () => image.value ?
      overrides.LogoUrl = image.value :
      delete overrides.LogoUrl;
    td.appendChild(image);
    tr.appendChild(td);

    td = document.createElement('td');
    const epgTz = document.createElement('input');
    epgTz.type = 'text';
    epgTz.setAttribute('is', 'emby-input');
    epgTz.placeholder = 'UTC';
    epgTz.value = overrides.EpgTimezone ?? '';
    epgTz.onchange = () => epgTz.value ?
      overrides.EpgTimezone = epgTz.value :
      delete overrides.EpgTimezone;
    td.appendChild(epgTz);
    tr.appendChild(td);

    td = document.createElement('td');
    const epgSource = document.createElement('select');
    epgSource.setAttribute('is', 'emby-select');
    const defaultOpt = document.createElement('option');
    defaultOpt.value = '';
    defaultOpt.textContent = 'Xtream (default)';
    epgSource.appendChild(defaultOpt);
    (epgSources || []).forEach(src => {
      const opt = document.createElement('option');
      opt.value = src.Id;
      opt.textContent = src.Name;
      epgSource.appendChild(opt);
    });
    epgSource.value = overrides.EpgSourceId ?? '';
    epgSource.onchange = () => epgSource.value ?
      overrides.EpgSourceId = epgSource.value :
      delete overrides.EpgSourceId;
    td.appendChild(epgSource);
    tr.appendChild(td);

    td = document.createElement('td');
    const xmltvChId = document.createElement('select');
    xmltvChId.setAttribute('is', 'emby-select');
    const emptyOpt = document.createElement('option');
    emptyOpt.value = '';
    emptyOpt.textContent = '-- Select --';
    xmltvChId.appendChild(emptyOpt);
    xmltvChId.value = overrides.XmltvChannelId ?? '';
    xmltvChId.onchange = () => xmltvChId.value ?
      overrides.XmltvChannelId = xmltvChId.value :
      delete overrides.XmltvChannelId;
    td.appendChild(xmltvChId);
    tr.appendChild(td);

    // When EPG source changes, fetch XMLTV channels for the dropdown
    const loadXmltvChannels = (sourceId) => {
      // Clear existing options except the first
      while (xmltvChId.options.length > 1) xmltvChId.remove(1);
      if (!sourceId) return;
      Xtream.fetchJson(`Xtream/EpgChannels/${sourceId}`).then(channels => {
        channels.forEach(ch => {
          const opt = document.createElement('option');
          opt.value = ch.Id;
          opt.textContent = `${ch.DisplayName} (${ch.Id})`;
          xmltvChId.appendChild(opt);
        });
        xmltvChId.value = overrides.XmltvChannelId ?? '';
      }).catch(() => {});
    };
    epgSource.addEventListener('change', () => loadXmltvChannels(epgSource.value));
    // Load initial channels if source is already set
    if (overrides.EpgSourceId) loadXmltvChannels(overrides.EpgSourceId);

    return tr;
  };

  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(2);

    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const table = view.querySelector('#LiveChannels');
    Dashboard.showLoadingMsg();
    Promise.all([
      getConfig,
      Xtream.fetchJson('Xtream/LiveTv'),
    ]).then(([config, channels]) => {
      const data = config.LiveTvOverrides;
      const epgSources = config.EpgSources || [];
      for (const channel of channels) {
        data[channel.Id] ??= {};
        const row = createChannelRow(channel, data[channel.Id], epgSources);
        table.appendChild(row);
      }
      Dashboard.hideLoadingMsg();

      view.querySelector('#XtreamLiveOverridesForm').addEventListener('submit', (e) => {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.LiveTvOverrides = Xtream.filter(
            data,
            overrides => Object.keys(overrides).length > 0
          );
          ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
            Dashboard.processPluginConfigurationUpdateResult(result);
          });
        });

        e.preventDefault();
        return false;
      });
    });
  }));
}